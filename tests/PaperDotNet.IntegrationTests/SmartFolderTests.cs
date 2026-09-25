using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Smart folders (TAX-08…10) and query aliases.</summary>
public sealed class SmartFolderTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(HttpClient Admin, string Tenant, Guid Workspace, Guid Tasks, Guid Docs, Guid Apollo, Guid Me, Guid Bob);

    /// <summary>A "Projects" term set with Apollo (child: Apollo Lander), a task list and a document list tagged by project.</summary>
    private async Task<Setup> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var admin = await ApiClient.CreateAsync(factory, tenant);
        var me = (await (await admin.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var bob = await PostIdAsync(admin, "/v1.0/users", new { userName = "bob", password = "bob-password-1" });
        var group = await PostIdAsync(admin, "/v1.0/termStore/groups", new { name = "Work" });
        var projects = await PostIdAsync(admin, "/v1.0/termStore/sets", new { groupId = group, name = "Projects" });
        var apollo = await PostIdAsync(admin, $"/v1.0/termStore/sets/{projects}/terms", new { name = "Apollo" });
        await PostIdAsync(admin, $"/v1.0/termStore/sets/{projects}/terms", new { name = "Lander", parentId = apollo });
        await PostIdAsync(admin, $"/v1.0/termStore/sets/{projects}/terms", new { name = "Gemini" });

        var ws = await admin.CreateWorkspaceAsync("Space");
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = bob, role = "member" }, Ct);
        var task = await admin.CreateContentTypeAsync("Job",
        [
            new { name = "project", type = "managedMetadata", termSetId = projects },
            new { name = "status", type = "choice", choices = new[] { "open", "done" } },
            new { name = "assignedTo", type = "person" },
            new { name = "due", type = "date" },
        ]);
        var doc = await admin.CreateContentTypeAsync("Paper",
        [
            new { name = "project", type = "managedMetadata", termSetId = projects },
            new { name = "counterparty", type = "text" },
            new { name = "issued", type = "date" },
        ]);
        return new Setup(admin, tenant, ws, await admin.CreateListAsync(ws, "Jobs", task), await admin.CreateListAsync(ws, "Papers", doc), apollo, me, bob);
    }

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ItemAsync(HttpClient client, Guid ws, Guid list, object fields) =>
        (await client.CreateItemAsync(ws, list, new { fields })).GetProperty("id").GetGuid();

    private static async Task<List<string>> TitlesAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("item").GetProperty("fields").GetProperty("title").GetString()!).ToList();
    }

    [Fact]
    public async Task A_smart_folder_shows_tagged_items_of_all_lists_including_child_terms()
    {
        var s = await SetupAsync("smart-terms");
        var lander = (await (await s.Admin.GetAsync($"/v1.0/termStore/sets/{(await (await s.Admin.GetAsync("/v1.0/termStore/sets", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Projects").GetProperty("id").GetGuid()}/terms?parentId={s.Apollo}", Ct)).ReadJsonAsync())
            .GetProperty("value")[0].GetProperty("id").GetGuid();
        await ItemAsync(s.Admin, s.Workspace, s.Tasks, new { title = "Launch plan", project = s.Apollo.ToString() });
        await ItemAsync(s.Admin, s.Workspace, s.Docs, new { title = "Lander spec", project = lander.ToString() });
        await ItemAsync(s.Admin, s.Workspace, s.Docs, new { title = "Gemini notes", project = "Gemini" });
        await ItemAsync(s.Admin, s.Workspace, s.Tasks, new { title = "Untagged" });
        await ItemAsync(s.Admin, s.Workspace, s.Docs, new { title = "Apollo budget", project = "Apollo" });

        var folder = await PostIdAsync(s.Admin, "/v1.0/smartFolders", new
        {
            name = "Project Apollo",
            workspaceId = s.Workspace,
            definition = new { terms = new[] { s.Apollo } },
        });
        Assert.Equal(["Apollo budget", "Lander spec", "Launch plan"], await TitlesAsync(s.Admin, $"/v1.0/smartFolders/{folder}/items"));

        // Paging across lists keeps the order.
        var first = await (await s.Admin.GetAsync($"/v1.0/smartFolders/{folder}/items?$top=2", Ct)).ReadJsonAsync();
        Assert.Equal(2, first.GetProperty("value").GetArrayLength());
        Assert.Equal(["Launch plan"], await TitlesAsync(s.Admin, first.GetProperty("@odata.nextLink").GetString()!));

        // Members see shared folders; only managers change them.
        var bob = await ApiClient.CreateAsync(factory, s.Tenant, "bob", "bob-password-1");
        Assert.Equal(3, (await TitlesAsync(bob, $"/v1.0/smartFolders/{folder}/items")).Count);
        var denied = await bob.PostAsJsonAsync("/v1.0/smartFolders", new { name = "Mine", workspaceId = s.Workspace, definition = new { } }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/v1.0/smartFolders/{folder}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Personal_folders_use_relative_values()
    {
        var s = await SetupAsync("smart-relative");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await ItemAsync(s.Admin, s.Workspace, s.Tasks, new { title = "Mine soon", status = "open", assignedTo = s.Me.ToString(), due = today.AddDays(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        await ItemAsync(s.Admin, s.Workspace, s.Tasks, new { title = "Mine later", status = "open", assignedTo = s.Me.ToString(), due = today.AddDays(40).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        await ItemAsync(s.Admin, s.Workspace, s.Tasks, new { title = "Mine done", status = "done", assignedTo = s.Me.ToString(), due = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        await ItemAsync(s.Admin, s.Workspace, s.Tasks, new { title = "Bob's", status = "open", assignedTo = s.Bob.ToString(), due = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });

        var folder = await PostIdAsync(s.Admin, "/v1.0/smartFolders", new
        {
            name = "Due this week",
            personal = true,
            definition = new { lists = new[] { "Jobs" }, filter = "fields/status eq 'open' and fields/assignedTo eq @me and fields/due le @next7Days" },
        });
        Assert.Equal(["Mine soon"], await TitlesAsync(s.Admin, $"/v1.0/smartFolders/{folder}/items"));

        // The aliases also work in the items API.
        var others = await s.Admin.GetAsync($"/v1.0/workspaces/{s.Workspace}/lists/{s.Tasks}/items?$filter=fields/assignedTo eq @me and fields/status eq 'done'", Ct);
        Assert.True(others.IsSuccessStatusCode, await others.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["Mine done"], await s.Admin.QueryTitlesAsync(s.Workspace, s.Tasks, "$filter=fields/assignedTo eq @me and fields/status eq 'done'"));

        // Personal folders are private.
        var bob = await ApiClient.CreateAsync(factory, s.Tenant, "bob", "bob-password-1");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/v1.0/smartFolders/{folder}", Ct)).StatusCode);
        Assert.DoesNotContain((await (await bob.GetAsync("/v1.0/smartFolders", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray(),
            f => f.GetProperty("id").GetGuid() == folder);
    }

    [Fact]
    public async Task Dropping_into_a_folder_classifies_and_removing_declassifies()
    {
        var s = await SetupAsync("smart-classify");
        var doc = await ItemAsync(s.Admin, s.Workspace, s.Docs, new { title = "Loose paper" });
        var task = await ItemAsync(s.Admin, s.Workspace, s.Tasks, new { title = "Loose task", status = "done" });
        var folder = await PostIdAsync(s.Admin, "/v1.0/smartFolders", new
        {
            name = "Apollo open",
            workspaceId = s.Workspace,
            definition = new { terms = new[] { s.Apollo }, filter = "fields/status eq 'open'", listTemplates = Array.Empty<string>() },
        });

        var dropped = await s.Admin.PostAsJsonAsync($"/v1.0/smartFolders/{folder}/items", new { workspaceId = s.Workspace, listId = s.Tasks, itemId = task }, Ct);
        Assert.Equal(HttpStatusCode.OK, dropped.StatusCode);
        var fields = (await dropped.ReadJsonAsync()).GetProperty("item").GetProperty("fields");
        Assert.Equal(s.Apollo.ToString(), fields.GetProperty("project").GetString());
        Assert.Equal("open", fields.GetProperty("status").GetString());
        Assert.Equal(["Loose task"], await TitlesAsync(s.Admin, $"/v1.0/smartFolders/{folder}/items"));

        // The papers list has no status field: its items cannot match this folder's filter.
        var mismatch = await s.Admin.PostAsJsonAsync($"/v1.0/smartFolders/{folder}/items", new { workspaceId = s.Workspace, listId = s.Docs, itemId = doc }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        // Creating in the folder.
        var created = await s.Admin.PostAsJsonAsync($"/v1.0/smartFolders/{folder}/items", new { workspaceId = s.Workspace, listId = s.Tasks, fields = new { title = "New in folder" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(["New in folder", "Loose task"], await TitlesAsync(s.Admin, $"/v1.0/smartFolders/{folder}/items"));

        var removed = await s.Admin.DeleteAsync($"/v1.0/smartFolders/{folder}/items/{task}?workspaceId={s.Workspace}&listId={s.Tasks}", Ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var after = (await removed.ReadJsonAsync()).GetProperty("item").GetProperty("fields");
        Assert.False(after.TryGetProperty("project", out _));
        Assert.Equal(["New in folder"], await TitlesAsync(s.Admin, $"/v1.0/smartFolders/{folder}/items"));
    }

    [Fact]
    public async Task Group_by_builds_virtual_sub_folders()
    {
        var s = await SetupAsync("smart-groups");
        await ItemAsync(s.Admin, s.Workspace, s.Docs, new { title = "A1", counterparty = "ACME", issued = "2025-03-01" });
        await ItemAsync(s.Admin, s.Workspace, s.Docs, new { title = "A2", counterparty = "ACME", issued = "2026-02-01" });
        await ItemAsync(s.Admin, s.Workspace, s.Docs, new { title = "B1", counterparty = "Beta", issued = "2026-05-01" });
        await ItemAsync(s.Admin, s.Workspace, s.Docs, new { title = "N1" });
        var folder = await PostIdAsync(s.Admin, "/v1.0/smartFolders", new
        {
            name = "By year",
            workspaceId = s.Workspace,
            definition = new { lists = new[] { "Papers" }, groupBy = new object[] { new { field = "issued", by = "year" }, new { field = "counterparty" } } },
        });

        async Task<List<(string? Value, int Count)>> GroupsAsync(string query)
        {
            var body = await (await s.Admin.GetAsync($"/v1.0/smartFolders/{folder}/groups{query}", Ct)).ReadJsonAsync();
            return body.GetProperty("value").EnumerateArray()
                .Select(g => (g.TryGetProperty("value", out var v) ? v.GetString() : null, g.GetProperty("count").GetInt32())).ToList();
        }

        Assert.Equal([("2025", 1), ("2026", 2), (null, 1)], await GroupsAsync(""));
        Assert.Equal([("ACME", 1), ("Beta", 1)], await GroupsAsync("?path=2026"));
        Assert.Equal(["A2"], await TitlesAsync(s.Admin, $"/v1.0/smartFolders/{folder}/items?path=2026&path=ACME"));
        Assert.Equal(["N1"], await TitlesAsync(s.Admin, $"/v1.0/smartFolders/{folder}/items?path="));
        Assert.Equal(["N1"], await s.Admin.QueryTitlesAsync(s.Workspace, s.Docs, "$filter=fields/issued eq null"));

        // Dropping into a sub-folder sets the settable level (counterparty; the year is computed).
        var created = await s.Admin.PostAsJsonAsync($"/v1.0/smartFolders/{folder}/items",
            new { workspaceId = s.Workspace, listId = s.Docs, fields = new { title = "B2", issued = "2026-06-01" }, path = new[] { "2026", "Beta" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal([("ACME", 1), ("Beta", 2)], await GroupsAsync("?path=2026"));
    }

    [Fact]
    public async Task Shared_smart_folders_travel_with_workspace_templates()
    {
        var s = await SetupAsync("smart-template-a");
        await PostIdAsync(s.Admin, "/v1.0/smartFolders", new
        {
            name = "Apollo work",
            workspaceId = s.Workspace,
            definition = new { lists = new[] { "Jobs" }, terms = new[] { s.Apollo }, filter = "fields/status eq 'open'" },
        });
        await PostIdAsync(s.Admin, "/v1.0/smartFolders", new { name = "Private", personal = true, workspaceId = s.Workspace, definition = new { } });
        var xml = await (await s.Admin.GetAsync($"/v1.0/provisioning/export?workspaceId={s.Workspace}", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("urn:paperdotnet:smartfolders:1", xml, StringComparison.Ordinal);
        Assert.Contains("Work/Projects/Apollo", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", xml, StringComparison.Ordinal);

        await factory.CreateTenantAsync("smart-template-b");
        var other = await ApiClient.CreateAsync(factory, "smart-template-b");
        var apply = await other.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.True(apply.IsSuccessStatusCode, await apply.Content.ReadAsStringAsync(Ct));
        var folder = (await (await other.GetAsync("/v1.0/smartFolders", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().Single();
        Assert.Equal("Apollo work", folder.GetProperty("name").GetString());
        var term = folder.GetProperty("definition").GetProperty("terms")[0].GetGuid();
        Assert.NotEqual(s.Apollo, term);
        var workspace = folder.GetProperty("workspaceId").GetGuid();
        var jobs = (await (await other.GetAsync($"/v1.0/workspaces/{workspace}/lists", Ct)).ReadJsonAsync()).EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Jobs").GetProperty("id").GetGuid();
        await ItemAsync(other, workspace, jobs, new { title = "Imported", project = "Apollo", status = "open" });
        Assert.Equal(["Imported"], await TitlesAsync(other, $"/v1.0/smartFolders/{folder.GetProperty("id").GetGuid()}/items"));

        var again = await other.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.Empty((await again.ReadJsonAsync()).GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task Smart_folders_of_another_tenant_are_invisible()
    {
        var s = await SetupAsync("smart-isolation-a");
        var folder = await PostIdAsync(s.Admin, "/v1.0/smartFolders", new { name = "Everything", workspaceId = s.Workspace, definition = new { } });
        await factory.CreateTenantAsync("smart-isolation-b");
        var other = await ApiClient.CreateAsync(factory, "smart-isolation-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/smartFolders/{folder}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/smartFolders/{folder}/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/smartFolders/{folder}", Ct)).StatusCode);
        Assert.Empty((await (await other.GetAsync("/v1.0/smartFolders", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray());
        var intrude = await other.PostAsJsonAsync("/v1.0/smartFolders", new { name = "X", workspaceId = s.Workspace, definition = new { } }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, intrude.StatusCode);
    }
}
