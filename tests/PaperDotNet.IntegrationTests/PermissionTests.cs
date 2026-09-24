using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Permission inheritance (IAM-07) and Home/Inbox (LST-07).</summary>
public sealed class PermissionTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(HttpClient Admin, string Tenant, Guid Workspace, Guid List, Dictionary<string, Guid> Users)
    {
        public string ListUrl => $"/v1.0/workspaces/{Workspace}/lists/{List}";
    }

    /// <summary>Workspace with members alice and bob, visitor vic, and a group "Auditors" containing bob.</summary>
    private async Task<Setup> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var admin = await ApiClient.CreateAsync(factory, tenant);
        var ws = await admin.CreateWorkspaceAsync("Team");
        var contentType = await admin.CreateContentTypeAsync("Doc", [new { name = "note", type = "text" }]);
        var list = await admin.CreateListAsync(ws, "Docs", contentType);
        var users = new Dictionary<string, Guid>();
        foreach (var (name, role) in new[] { ("alice", "member"), ("bob", "member"), ("vic", "visitor") })
        {
            var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
            users[name] = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
            await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = users[name], role }, Ct);
        }

        var group = await admin.PostAsJsonAsync("/v1.0/groups", new { name = "Auditors" }, Ct);
        users["auditors"] = (await group.ReadJsonAsync()).GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync($"/v1.0/groups/{users["auditors"]}/members", new { userId = users["bob"] }, Ct);
        return new Setup(admin, tenant, ws, list, users);
    }

    private Task<HttpClient> AsAsync(Setup setup, string user) => ApiClient.CreateAsync(factory, setup.Tenant, user, $"{user}-password-1");

    private static async Task<Guid> CreateAsync(HttpClient client, Setup setup, string title, Guid? parentId = null, bool isFolder = false) =>
        (await client.CreateItemAsync(setup.Workspace, setup.List, new { parentId, isFolder, fields = new { title } })).GetProperty("id").GetGuid();

    private static async Task<List<string>> TitlesAsync(HttpClient client, Setup setup) =>
        (await client.QueryTitlesAsync(setup.Workspace, setup.List, "")).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Folders_with_unique_permissions_hide_and_protect_their_content()
    {
        var setup = await SetupAsync("perm-folder");
        var folder = await CreateAsync(setup.Admin, setup, "HR", isFolder: true);
        var sub = await CreateAsync(setup.Admin, setup, "HR/Sub", folder, isFolder: true);
        var secret = await CreateAsync(setup.Admin, setup, "Salaries", sub);
        await CreateAsync(setup.Admin, setup, "Public");

        var broken = await setup.Admin.PostAsJsonAsync($"{setup.ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, broken.StatusCode);
        var grant = await setup.Admin.PutAsJsonAsync($"{setup.ListUrl}/items/{folder}/permissions/grants",
            new { grants = new object[] { new { principalType = "group", principalId = setup.Users["auditors"], level = "read" } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        var alice = await AsAsync(setup, "alice");
        var bob = await AsAsync(setup, "bob");
        Assert.Equal(["Public"], await TitlesAsync(alice, setup));
        Assert.Equal(["HR", "HR/Sub", "Public", "Salaries"], await TitlesAsync(bob, setup));
        Assert.Equal(["HR", "HR/Sub", "Public", "Salaries"], await TitlesAsync(setup.Admin, setup));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"{setup.ListUrl}/items/{secret}", Ct)).StatusCode);

        // Read-only through the group: no new items, no edits.
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostItemAsync(setup.Workspace, setup.List, new { parentId = sub, fields = new { title = "x" } })).StatusCode);
        var url = $"{setup.ListUrl}/items/{secret}";
        var etag = (await bob.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields = new { note = "x" } })).StatusCode);

        // New items in the folder inherit its scope.
        var added = await CreateAsync(setup.Admin, setup, "Bonus", sub);
        Assert.DoesNotContain("Bonus", await TitlesAsync(alice, setup));

        var permissions = await (await bob.GetAsync($"{setup.ListUrl}/items/{added}/permissions", Ct)).ReadJsonAsync();
        Assert.Equal("item", permissions.GetProperty("inheritsFrom").GetString());
        Assert.Equal(folder, permissions.GetProperty("inheritsFromId").GetGuid());
        Assert.Equal("read", permissions.GetProperty("effectiveLevel").GetString());
        Assert.False(permissions.TryGetProperty("grants", out var grants) && grants.ValueKind != JsonValueKind.Null);

        // Resetting restores the workspace permissions for the whole subtree.
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Admin.PostAsync($"{setup.ListUrl}/items/{folder}/permissions/resetInheritance", null, Ct)).StatusCode);
        Assert.Equal(["Bonus", "HR", "HR/Sub", "Public", "Salaries"], await TitlesAsync(alice, setup));
    }

    [Fact]
    public async Task Breaking_inheritance_copies_grants_and_members_can_be_elevated()
    {
        var setup = await SetupAsync("perm-copy");
        var item = await CreateAsync(setup.Admin, setup, "Plan");
        var vic = await AsAsync(setup, "vic");
        var url = $"{setup.ListUrl}/items/{item}";

        // Visitors read but cannot edit.
        var etag = (await vic.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.Forbidden, (await vic.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields = new { note = "x" } })).StatusCode);

        var broken = await (await setup.Admin.PostAsJsonAsync($"{url}/permissions/breakInheritance", new { }, Ct)).ReadJsonAsync();
        var copied = broken.GetProperty("grants").EnumerateArray().ToDictionary(g => g.GetProperty("principalId").GetGuid(), g => g.GetProperty("level").GetString());
        Assert.Equal("read", copied[setup.Users["vic"]]);
        Assert.Equal("contribute", copied[setup.Users["alice"]]);

        var grants = copied.Select(g => new { principalType = "user", principalId = g.Key, level = g.Key == setup.Users["vic"] ? "contribute" : g.Value }).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await setup.Admin.PutAsJsonAsync($"{url}/permissions/grants", new { grants }, Ct)).StatusCode);
        etag = (await vic.GetAsync(url, Ct)).Headers.ETag!.Tag; // breaking inheritance changed the item
        Assert.Equal(HttpStatusCode.OK, (await vic.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields = new { note = "edited by vic" } })).StatusCode);

        // Members without Manage cannot change permissions.
        var alice = await AsAsync(setup, "alice");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync($"{url}/permissions/resetInheritance", null, Ct)).StatusCode);
        var invalid = await setup.Admin.PutAsJsonAsync($"{url}/permissions/grants",
            new { grants = new[] { new { principalType = "user", principalId = Guid.NewGuid(), level = "read" } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Lists_with_unique_permissions_are_hidden_without_a_grant()
    {
        var setup = await SetupAsync("perm-list");
        await CreateAsync(setup.Admin, setup, "Budget");
        await setup.Admin.PostAsJsonAsync($"{setup.ListUrl}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        await setup.Admin.PutAsJsonAsync($"{setup.ListUrl}/permissions/grants",
            new { grants = new[] { new { principalType = "user", principalId = setup.Users["bob"], level = "contribute" } } }, Ct);

        var alice = await AsAsync(setup, "alice");
        var bob = await AsAsync(setup, "bob");
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(setup.ListUrl, Ct)).StatusCode);
        var aliceLists = await (await alice.GetAsync($"/v1.0/workspaces/{setup.Workspace}/lists", Ct)).ReadJsonAsync();
        Assert.Equal(0, aliceLists.GetArrayLength());
        Assert.Equal(["Budget"], await TitlesAsync(bob, setup));
        Assert.Equal(HttpStatusCode.Created, (await bob.PostItemAsync(setup.Workspace, setup.List, new { fields = new { title = "By bob" } })).StatusCode);
    }

    [Fact]
    public async Task Moving_an_item_into_a_protected_folder_changes_its_visibility()
    {
        var setup = await SetupAsync("perm-move");
        var folder = await CreateAsync(setup.Admin, setup, "Private", isFolder: true);
        await setup.Admin.PostAsJsonAsync($"{setup.ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        var item = await CreateAsync(setup.Admin, setup, "Draft");
        var alice = await AsAsync(setup, "alice");
        Assert.Contains("Draft", await TitlesAsync(alice, setup));

        // Alice cannot move items into a folder she cannot contribute to.
        var url = $"{setup.ListUrl}/items/{item}";
        var etag = (await alice.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { parentId = folder })).StatusCode);

        etag = (await setup.Admin.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await setup.Admin.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { parentId = folder })).StatusCode);
        Assert.DoesNotContain("Draft", await TitlesAsync(alice, setup));
    }

    [Fact]
    public async Task Every_user_has_a_home_with_documents_and_inbox()
    {
        var setup = await SetupAsync("perm-home");
        var alice = await AsAsync(setup, "alice");

        var home = await (await alice.GetAsync("/v1.0/me/home", Ct)).ReadJsonAsync();
        var again = await (await alice.GetAsync("/v1.0/me/home", Ct)).ReadJsonAsync();
        Assert.Equal(home.GetProperty("inboxListId").GetGuid(), again.GetProperty("inboxListId").GetGuid());
        var ws = home.GetProperty("workspaceId").GetGuid();
        var inbox = home.GetProperty("inboxListId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, (await alice.PostItemAsync(ws, inbox, new { fields = new { title = "scan.pdf" } })).StatusCode);
        var workspace = await (await alice.GetAsync($"/v1.0/workspaces/{ws}", Ct)).ReadJsonAsync();
        Assert.True(workspace.GetProperty("isPersonal").GetBoolean());

        // Private to alice; the system libraries and the workspace itself cannot be deleted.
        var bob = await AsAsync(setup, "bob");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/v1.0/workspaces/{ws}/lists/{inbox}/items", Ct)).StatusCode);
        Assert.NotEqual(ws, (await (await bob.GetAsync("/v1.0/me/home", Ct)).ReadJsonAsync()).GetProperty("workspaceId").GetGuid());
        var listUrl = $"/v1.0/workspaces/{ws}/lists/{inbox}";
        Assert.Equal(HttpStatusCode.Conflict, (await alice.SendWithEtagAsync(HttpMethod.Delete, listUrl, (await alice.GetAsync(listUrl, Ct)).Headers.ETag!.Tag)).StatusCode);
        var wsUrl = $"/v1.0/workspaces/{ws}";
        Assert.Equal(HttpStatusCode.Conflict, (await alice.SendWithEtagAsync(HttpMethod.Delete, wsUrl, (await alice.GetAsync(wsUrl, Ct)).Headers.ETag!.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync($"{wsUrl}/members", new { userId = setup.Users["bob"] }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Permissions_are_isolated_per_tenant()
    {
        var setup = await SetupAsync("perm-isolation");
        var item = await CreateAsync(setup.Admin, setup, "A");
        await factory.CreateTenantAsync("perm-isolation-b");
        var other = await ApiClient.CreateAsync(factory, "perm-isolation-b");

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{setup.ListUrl}/permissions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{setup.ListUrl}/items/{item}/permissions/breakInheritance", null, Ct)).StatusCode);
    }
}
