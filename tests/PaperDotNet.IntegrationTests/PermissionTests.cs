using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Permission inheritance (IAM-07, ADR-0035): workspace roles, lists, folders and items.</summary>
public sealed class PermissionTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private string _workspace = "";
    private string _list = "";
    private readonly Dictionary<string, string> _users = [];

    private string ListUrl => $"/v1.0/workspaces/{_workspace}/lists/{_list}";

    /// <summary>Workspace with members alice and bob, visitor vic, and a group "Auditors" containing bob.</summary>
    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Team");
        _list = (await Api.CreateListAsync(_admin, _workspace, "Docs", new[] { new { name = "note", type = "text" } })).Id();
        foreach (var (name, role) in new[] { ("alice", "member"), ("bob", "member"), ("vic", "visitor") })
        {
            _users[name] = await UserAsync(name);
            Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/members", new { userId = _users[name], role }, Ct)).StatusCode);
        }

        using var group = await _admin.PostAsJsonAsync("/v1.0/groups", new { name = "Auditors" }, Ct);
        _users["auditors"] = (await group.JsonAsync(HttpStatusCode.Created)).Id();
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/groups/{_users["auditors"]}/members", new { userId = _users["bob"] }, Ct)).StatusCode);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<string> UserAsync(string name)
    {
        using var created = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
        return (await created.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private Task<HttpClient> AsAsync(string user) => _host.SignInAsync(user, $"{user}-password-1");

    private async Task<string> CreateAsync(HttpClient client, string title, string? parentId = null, bool isFolder = false)
    {
        using var response = await client.PostAsJsonAsync($"{ListUrl}/items", new { parentId, isFolder, fields = new { title } }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private async Task<HttpResponseMessage> PostItemAsync(HttpClient client, object body) => await client.PostAsJsonAsync($"{ListUrl}/items", body, Ct);

    private async Task<List<string>> TitlesAsync(HttpClient client) =>
        [.. (await (await client.GetAsync($"{ListUrl}/items", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
            .Select(i => i.GetProperty("fields").GetProperty("title").GetString()!).Order(StringComparer.Ordinal)];

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string url, object body)
    {
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        return await client.SendAsync(Api.Patch(url, body, etag), Ct);
    }

    [Fact]
    public async Task Folders_with_unique_permissions_hide_and_protect_their_content()
    {
        var folder = await CreateAsync(_admin, "HR", isFolder: true);
        var sub = await CreateAsync(_admin, "HR/Sub", folder, isFolder: true);
        var secret = await CreateAsync(_admin, "Salaries", sub);
        await CreateAsync(_admin, "Public");

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"{ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"{ListUrl}/items/{folder}/permissions/grants",
            new { grants = new object[] { new { principalType = "group", principalId = _users["auditors"], level = "read" } } }, Ct)).StatusCode);

        var alice = await AsAsync("alice");
        var bob = await AsAsync("bob");
        Assert.Equal(["Public"], await TitlesAsync(alice));
        Assert.Equal(["HR", "HR/Sub", "Public", "Salaries"], await TitlesAsync(bob));
        Assert.Equal(["HR", "HR/Sub", "Public", "Salaries"], await TitlesAsync(_admin));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"{ListUrl}/items/{secret}", Ct)).StatusCode);

        // Read-only through the group: no new items, no edits.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostItemAsync(bob, new { parentId = sub, fields = new { title = "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(bob, $"{ListUrl}/items/{secret}", new { fields = new { note = "x" } })).StatusCode);

        // New items in the folder inherit its scope.
        var added = await CreateAsync(_admin, "Bonus", sub);
        Assert.DoesNotContain("Bonus", await TitlesAsync(alice));

        var permissions = await (await bob.GetAsync($"{ListUrl}/items/{added}/permissions", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("item", permissions.GetProperty("inheritsFrom").GetString());
        Assert.Equal(folder, permissions.GetProperty("inheritsFromId").GetString());
        Assert.Equal("read", permissions.GetProperty("effectiveLevel").GetString());
        Assert.False(permissions.TryGetProperty("grants", out var grants) && grants.ValueKind != JsonValueKind.Null);

        // Resetting restores the workspace permissions for the whole subtree.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"{ListUrl}/items/{folder}/permissions/resetInheritance", null, Ct)).StatusCode);
        Assert.Equal(["Bonus", "HR", "HR/Sub", "Public", "Salaries"], await TitlesAsync(alice));
    }

    [Fact]
    public async Task Breaking_inheritance_copies_grants_and_members_can_be_elevated()
    {
        var item = await CreateAsync(_admin, "Plan");
        var vic = await AsAsync("vic");
        var url = $"{ListUrl}/items/{item}";

        // Visitors read but cannot edit.
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(vic, url, new { fields = new { note = "x" } })).StatusCode);

        // The copy names the workspace roles, not their members (ADR-0035).
        var broken = await (await _admin.PostAsJsonAsync($"{url}/permissions/breakInheritance", new { }, Ct)).JsonAsync(HttpStatusCode.OK);
        var copied = broken.GetProperty("grants").EnumerateArray()
            .ToDictionary(g => g.GetProperty("principalType").GetString()!, g => (Id: g.GetProperty("principalId").GetString()!, Level: g.GetProperty("level").GetString()));
        Assert.Equal(["workspaceMembers", "workspaceOwners", "workspaceVisitors"], copied.Keys.Order(StringComparer.Ordinal));
        Assert.All(copied.Values, g => Assert.Equal(_workspace, g.Id));
        Assert.Equal("read", copied["workspaceVisitors"].Level);
        Assert.Equal("contribute", copied["workspaceMembers"].Level);
        Assert.Equal("manage", copied["workspaceOwners"].Level);

        var grants = copied.Select(g => new { principalType = g.Key, principalId = g.Value.Id, level = g.Value.Level })
            .Append(new { principalType = "user", principalId = _users["vic"], level = (string?)"contribute" })
            .ToArray();
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"{url}/permissions/grants", new { grants }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(vic, url, new { fields = new { note = "edited by vic" } })).StatusCode);

        // Members without Manage cannot change permissions; grants must name known principals.
        var alice = await AsAsync("alice");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync($"{url}/permissions/resetInheritance", null, Ct)).StatusCode);
        var invalid = await _admin.PutAsJsonAsync($"{url}/permissions/grants",
            new { grants = new[] { new { principalType = "user", principalId = Guid.NewGuid(), level = "read" } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsJsonAsync($"{url}/permissions/breakInheritance", new { }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Workspace_roles_are_principals_and_owners_keep_full_control()
    {
        await CreateAsync(_admin, "Budget");

        var inherited = await (await _admin.GetAsync($"{ListUrl}/permissions", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("workspace", inherited.GetProperty("inheritsFrom").GetString());
        Assert.Equal(3, inherited.GetProperty("grants").GetArrayLength());

        // Only members (and owners) keep access: visitors lose it, members who join later get it.
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PutAsJsonAsync($"{ListUrl}/permissions/grants", new { grants = Array.Empty<object>() }, Ct)).StatusCode);
        await _admin.PostAsJsonAsync($"{ListUrl}/permissions/breakInheritance", new { }, Ct);
        var replaced = await _admin.PutAsJsonAsync($"{ListUrl}/permissions/grants",
            new { grants = new[] { new { principalType = "workspaceMembers", principalId = _workspace, level = "contribute" } } }, Ct);
        var response = await replaced.JsonAsync(HttpStatusCode.OK);
        Assert.Equal(["workspaceMembers:contribute", "workspaceOwners:manage"], response.GetProperty("grants").EnumerateArray()
            .Select(g => $"{g.GetProperty("principalType").GetString()}:{g.GetProperty("level").GetString()}").Order(StringComparer.Ordinal));

        var vic = await AsAsync("vic");
        Assert.Equal(HttpStatusCode.NotFound, (await vic.GetAsync(ListUrl, Ct)).StatusCode);
        var late = await UserAsync("late");
        await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/members", new { userId = late, role = "member" }, Ct);
        Assert.Equal(["Budget"], await TitlesAsync(await AsAsync("late")));

        // Owners cannot be removed or lowered; roles need the list's workspace.
        var lowered = await _admin.PutAsJsonAsync($"{ListUrl}/permissions/grants",
            new { grants = new[] { new { principalType = "workspaceOwners", principalId = _workspace, level = "read" } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, lowered.StatusCode);
        var otherWorkspace = await _admin.PutAsJsonAsync($"{ListUrl}/permissions/grants",
            new { grants = new[] { new { principalType = "workspaceVisitors", principalId = Guid.NewGuid().ToString(), level = "read" } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, otherWorkspace.StatusCode);

        // Resetting brings the three role entries back.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"{ListUrl}/permissions/resetInheritance", null, Ct)).StatusCode);
        Assert.Equal(["Budget"], await TitlesAsync(vic));
    }

    [Fact]
    public async Task Membership_and_group_changes_apply_at_once()
    {
        var folder = await CreateAsync(_admin, "Audit", isFolder: true);
        await CreateAsync(_admin, "Findings", folder);
        await _admin.PostAsJsonAsync($"{ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        await _admin.PutAsJsonAsync($"{ListUrl}/items/{folder}/permissions/grants",
            new { grants = new object[] { new { principalType = "group", principalId = _users["auditors"], level = "read" } } }, Ct);

        // The principal set is cached per user: both changes below must evict it.
        var alice = await AsAsync("alice");
        Assert.Empty(await TitlesAsync(alice));
        await _admin.PostAsJsonAsync($"/v1.0/groups/{_users["auditors"]}/members", new { userId = _users["alice"] }, Ct);
        Assert.Equal(["Audit", "Findings"], await TitlesAsync(alice));

        var vic = await AsAsync("vic");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostItemAsync(vic, new { fields = new { title = "By vic" } })).StatusCode);
        await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/members", new { userId = _users["vic"], role = "member" }, Ct);
        Assert.Equal(HttpStatusCode.Created, (await PostItemAsync(vic, new { fields = new { title = "By vic" } })).StatusCode);
    }

    [Fact]
    public async Task Lists_with_unique_permissions_are_hidden_without_a_grant()
    {
        await CreateAsync(_admin, "Budget");
        await _admin.PostAsJsonAsync($"{ListUrl}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        await _admin.PutAsJsonAsync($"{ListUrl}/permissions/grants",
            new { grants = new[] { new { principalType = "user", principalId = _users["bob"], level = "contribute" } } }, Ct);

        var alice = await AsAsync("alice");
        var bob = await AsAsync("bob");
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(ListUrl, Ct)).StatusCode);
        Assert.Empty((await (await alice.GetAsync($"/v1.0/workspaces/{_workspace}/lists", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());
        Assert.Equal(["Budget"], await TitlesAsync(bob));
        Assert.Equal(HttpStatusCode.Created, (await PostItemAsync(bob, new { fields = new { title = "By bob" } })).StatusCode);
    }

    [Fact]
    public async Task Moving_items_and_folders_into_a_protected_folder_changes_their_visibility()
    {
        var folder = await CreateAsync(_admin, "Private", isFolder: true);
        await _admin.PostAsJsonAsync($"{ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        var item = await CreateAsync(_admin, "Draft");
        var moving = await CreateAsync(_admin, "Moving", isFolder: true);
        await CreateAsync(_admin, "Inside", moving);
        var alice = await AsAsync("alice");
        Assert.Equal(["Draft", "Inside", "Moving"], await TitlesAsync(alice));

        // Alice cannot move items into a folder she cannot see.
        var url = $"{ListUrl}/items/{item}";
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(alice, url, new { parentId = folder })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, url, new { parentId = folder })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, $"{ListUrl}/items/{moving}", new { parentId = folder })).StatusCode);
        Assert.Empty(await TitlesAsync(alice));

        // Back at the root, the folder and its contents are visible again.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, $"{ListUrl}/items/{moving}", new { parentId = (string?)null })).StatusCode);
        Assert.Equal(["Inside", "Moving"], await TitlesAsync(alice));
    }

    [Fact]
    public async Task Permissions_are_isolated_per_tenant()
    {
        var item = await CreateAsync(_admin, "A");
        var other = await _host.CreateTenantAsync("other");

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{ListUrl}/permissions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{ListUrl}/permissions/breakInheritance", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{ListUrl}/permissions/grants", new { grants = Array.Empty<object>() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{ListUrl}/items/{item}/permissions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{ListUrl}/items/{item}/permissions/breakInheritance", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{ListUrl}/items/{item}/permissions/resetInheritance", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Interrupted_folder_moves_are_completed_by_their_message()
    {
        var privateFolder = await CreateAsync(_admin, "Private", isFolder: true);
        await _admin.PostAsJsonAsync($"{ListUrl}/items/{privateFolder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        var moved = await CreateAsync(_admin, "Moved", isFolder: true);
        var child = Guid.Parse(await CreateAsync(_admin, "Child", moved));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(_admin, $"{ListUrl}/items/{moved}", new { parentId = privateFolder })).StatusCode);

        var tenantId = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
        var (list, folderScope) = (Guid.Parse(_list), Guid.Parse(privateFolder));
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
        Assert.Equal(folderScope, (await db.Items.AsNoTracking().SingleAsync(i => i.Id == child, Ct)).ScopeId);

        // As if the request had stopped right after saving the folder: the child still has the old scope.
        await db.Items.Where(i => i.Id == child).ExecuteUpdateAsync(u => u.SetProperty(i => i.ScopeId, list), Ct);
        var message = new CompleteFolderScopeChange(tenantId, list, Guid.Parse(moved), list, folderScope);
        var mover = scope.ServiceProvider.GetRequiredService<ScopeMover>();
        await FolderScopeSubscriber.Handle(message, mover, Ct);
        Assert.Equal(folderScope, (await db.Items.AsNoTracking().SingleAsync(i => i.Id == child, Ct)).ScopeId);

        // A stale message (the folder moved again since) changes nothing.
        await FolderScopeSubscriber.Handle(message with { NewScopeId = Guid.NewGuid() }, mover, Ct);
        Assert.Equal(folderScope, (await db.Items.AsNoTracking().SingleAsync(i => i.Id == child, Ct)).ScopeId);
    }
}
