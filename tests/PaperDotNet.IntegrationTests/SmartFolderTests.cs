using System.Globalization;
using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Smart folders (TAX-08…10): saved views over many lists, terms with their children, sub-folders, classification.</summary>
public sealed class SmartFolderTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private sealed record Setup(string Workspace, string Jobs, string Papers, string Projects, string Apollo, string Lander, string Me, string Bob);

    /// <summary>A "Projects" term set with Apollo (child: Lander) and Gemini, a job list and a paper list tagged by project.</summary>
    private async Task<Setup> SetupAsync()
    {
        var me = (await GetAsync(_admin, "/v1.0/me")).Id();
        var bob = await PostIdAsync(_admin, "/v1.0/users", new { userName = "bob", password = "bob-password-1" });
        var group = await PostIdAsync(_admin, "/v1.0/termStore/groups", new { name = "Work" });
        var projects = await PostIdAsync(_admin, "/v1.0/termStore/sets", new { groupId = group, name = "Projects" });
        var apollo = await PostIdAsync(_admin, $"/v1.0/termStore/sets/{projects}/terms", new { name = "Apollo" });
        var lander = await PostIdAsync(_admin, $"/v1.0/termStore/sets/{projects}/terms", new { name = "Lander", parentId = apollo });
        await PostIdAsync(_admin, $"/v1.0/termStore/sets/{projects}/terms", new { name = "Gemini" });

        var ws = await Api.CreateWorkspaceAsync(_admin, "Space");
        using (var member = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = bob, role = "member" }, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, member.StatusCode);
        }

        var jobs = (await Api.CreateListAsync(_admin, ws, "Jobs", new object[]
        {
            new { name = "project", displayName = "Project", type = "managedMetadata", termSetId = projects },
            new { name = "status", displayName = "Status", type = "choice", choices = new[] { "open", "done" } },
            new { name = "assignedTo", displayName = "Assigned to", type = "person" },
            new { name = "due", displayName = "Due", type = "date" },
        })).Id();
        var papers = (await Api.CreateListAsync(_admin, ws, "Papers", new object[]
        {
            new { name = "project", displayName = "Project", type = "managedMetadata", termSetId = projects },
            new { name = "counterparty", displayName = "Counterparty", type = "text" },
            new { name = "issued", displayName = "Issued", type = "date" },
        })).Id();
        return new Setup(ws, jobs, papers, projects, apollo, lander, me, bob);
    }

    private static async Task<string> PostIdAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        return await response.JsonAsync(HttpStatusCode.OK);
    }

    private static async Task<string> ItemAsync(HttpClient client, string ws, string list, object fields) => (await Api.CreateItemAsync(client, ws, list, fields)).Id();

    private static async Task<List<string>> TitlesAsync(HttpClient client, string url) =>
        [.. (await GetAsync(client, url)).GetProperty("value").EnumerateArray().Select(e => e.GetProperty("item").GetProperty("fields").GetProperty("title").GetString()!)];

    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_smart_folder_shows_tagged_items_of_all_lists_including_child_terms()
    {
        var s = await SetupAsync();
        await ItemAsync(_admin, s.Workspace, s.Jobs, new { title = "Launch plan", project = s.Apollo });
        await ItemAsync(_admin, s.Workspace, s.Papers, new { title = "Lander spec", project = s.Lander });
        await ItemAsync(_admin, s.Workspace, s.Papers, new { title = "Gemini notes", project = "Gemini" });
        await ItemAsync(_admin, s.Workspace, s.Jobs, new { title = "Untagged" });
        await ItemAsync(_admin, s.Workspace, s.Papers, new { title = "Apollo budget", project = "Apollo" });

        var folder = await PostIdAsync(_admin, "/v1.0/smartFolders", new { name = "Project Apollo", workspaceId = s.Workspace, definition = new { terms = new[] { s.Apollo } } });
        Assert.Equal(["Apollo budget", "Lander spec", "Launch plan"], await TitlesAsync(_admin, $"/v1.0/smartFolders/{folder}/items"));

        // Paging across lists keeps the order.
        var first = await GetAsync(_admin, $"/v1.0/smartFolders/{folder}/items?$top=2");
        Assert.Equal(2, first.GetProperty("value").GetArrayLength());
        Assert.Equal(["Launch plan"], await TitlesAsync(_admin, first.GetProperty("@odata.nextLink").GetString()!));

        // Members see shared folders; only managers change them.
        var bob = await _host.SignInAsync("bob", "bob-password-1");
        Assert.Equal(3, (await TitlesAsync(bob, $"/v1.0/smartFolders/{folder}/items")).Count);
        using var denied = await bob.PostAsJsonAsync("/v1.0/smartFolders", new { name = "Mine", workspaceId = s.Workspace, definition = new { } }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/v1.0/smartFolders/{folder}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Personal_folders_use_relative_values()
    {
        var s = await SetupAsync();
        await ItemAsync(_admin, s.Workspace, s.Jobs, new { title = "Mine soon", status = "open", assignedTo = s.Me, due = Day(2) });
        await ItemAsync(_admin, s.Workspace, s.Jobs, new { title = "Mine later", status = "open", assignedTo = s.Me, due = Day(40) });
        await ItemAsync(_admin, s.Workspace, s.Jobs, new { title = "Mine done", status = "done", assignedTo = s.Me, due = Day(0) });
        await ItemAsync(_admin, s.Workspace, s.Jobs, new { title = "Bob's", status = "open", assignedTo = s.Bob, due = Day(0) });

        var folder = await PostIdAsync(_admin, "/v1.0/smartFolders", new
        {
            name = "Due this week",
            personal = true,
            definition = new { lists = new[] { "Jobs" }, filter = "fields/status eq 'open' and fields/assignedTo eq @me and fields/due le @next7Days" },
        });
        Assert.Equal(["Mine soon"], await TitlesAsync(_admin, $"/v1.0/smartFolders/{folder}/items"));

        // Personal folders are private.
        var bob = await _host.SignInAsync("bob", "bob-password-1");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/v1.0/smartFolders/{folder}", Ct)).StatusCode);
        Assert.DoesNotContain((await GetAsync(bob, "/v1.0/smartFolders")).GetProperty("value").EnumerateArray(), f => f.Id() == folder);
    }

    [Fact]
    public async Task Dropping_into_a_folder_classifies_and_removing_declassifies()
    {
        var s = await SetupAsync();
        var doc = await ItemAsync(_admin, s.Workspace, s.Papers, new { title = "Loose paper" });
        var task = await ItemAsync(_admin, s.Workspace, s.Jobs, new { title = "Loose task", status = "done" });
        var folder = await PostIdAsync(_admin, "/v1.0/smartFolders", new
        {
            name = "Apollo open",
            workspaceId = s.Workspace,
            definition = new { terms = new[] { s.Apollo }, filter = "fields/status eq 'open'" },
        });

        using var dropped = await _admin.PostAsJsonAsync($"/v1.0/smartFolders/{folder}/items", new { workspaceId = s.Workspace, listId = s.Jobs, itemId = task }, Ct);
        var fields = (await dropped.JsonAsync(HttpStatusCode.OK)).GetProperty("item").GetProperty("fields");
        Assert.Equal(s.Apollo, fields.GetProperty("project").GetString());
        Assert.Equal("open", fields.GetProperty("status").GetString());
        Assert.Equal(["Loose task"], await TitlesAsync(_admin, $"/v1.0/smartFolders/{folder}/items"));

        // The papers list has no status field: its items cannot match this folder's filter.
        using var mismatch = await _admin.PostAsJsonAsync($"/v1.0/smartFolders/{folder}/items", new { workspaceId = s.Workspace, listId = s.Papers, itemId = doc }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        // Creating in the folder.
        using var created = await _admin.PostAsJsonAsync($"/v1.0/smartFolders/{folder}/items", new { workspaceId = s.Workspace, listId = s.Jobs, fields = new { title = "New in folder" } }, Ct);
        await created.JsonAsync(HttpStatusCode.Created);
        Assert.Equal(["New in folder", "Loose task"], await TitlesAsync(_admin, $"/v1.0/smartFolders/{folder}/items"));

        using var removed = await _admin.DeleteAsync($"/v1.0/smartFolders/{folder}/items/{task}?workspaceId={s.Workspace}&listId={s.Jobs}", Ct);
        var after = (await removed.JsonAsync(HttpStatusCode.OK)).GetProperty("item").GetProperty("fields");
        Assert.False(after.TryGetProperty("project", out _));
        Assert.Equal(["New in folder"], await TitlesAsync(_admin, $"/v1.0/smartFolders/{folder}/items"));
    }

    [Fact]
    public async Task Group_by_builds_virtual_sub_folders()
    {
        var s = await SetupAsync();
        await ItemAsync(_admin, s.Workspace, s.Papers, new { title = "A1", counterparty = "ACME", issued = "2025-03-01" });
        await ItemAsync(_admin, s.Workspace, s.Papers, new { title = "A2", counterparty = "ACME", issued = "2026-02-01" });
        await ItemAsync(_admin, s.Workspace, s.Papers, new { title = "B1", counterparty = "Beta", issued = "2026-05-01" });
        await ItemAsync(_admin, s.Workspace, s.Papers, new { title = "N1" });
        var folder = await PostIdAsync(_admin, "/v1.0/smartFolders", new
        {
            name = "By year",
            workspaceId = s.Workspace,
            definition = new { lists = new[] { "Papers" }, groupBy = new object[] { new { field = "issued", by = "year" }, new { field = "counterparty" } } },
        });

        async Task<List<(string? Value, int Count)>> GroupsAsync(string query) =>
            [.. (await GetAsync(_admin, $"/v1.0/smartFolders/{folder}/groups{query}")).GetProperty("value").EnumerateArray()
                .Select(g => (g.TryGetProperty("value", out var v) ? v.GetString() : null, g.GetProperty("count").GetInt32()))];

        Assert.Equal([("2025", 1), ("2026", 2), (null, 1)], await GroupsAsync(""));
        Assert.Equal([("ACME", 1), ("Beta", 1)], await GroupsAsync("?path=2026"));
        Assert.Equal(["A2"], await TitlesAsync(_admin, $"/v1.0/smartFolders/{folder}/items?path=2026&path=ACME"));
        Assert.Equal(["N1"], await TitlesAsync(_admin, $"/v1.0/smartFolders/{folder}/items?path="));

        // Dropping into a sub-folder sets the settable level (counterparty; the year is computed).
        using var created = await _admin.PostAsJsonAsync($"/v1.0/smartFolders/{folder}/items",
            new { workspaceId = s.Workspace, listId = s.Papers, fields = new { title = "B2", issued = "2026-06-01" }, path = new[] { "2026", "Beta" } }, Ct);
        await created.JsonAsync(HttpStatusCode.Created);
        Assert.Equal([("ACME", 1), ("Beta", 2)], await GroupsAsync("?path=2026"));
    }

    [Fact]
    public async Task Smart_folders_of_another_tenant_are_invisible()
    {
        var s = await SetupAsync();
        var folder = await PostIdAsync(_admin, "/v1.0/smartFolders", new { name = "Everything", workspaceId = s.Workspace, definition = new { } });
        var other = await _host.CreateTenantAsync("smart-isolation-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/smartFolders/{folder}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/smartFolders/{folder}/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/smartFolders/{folder}", Ct)).StatusCode);
        Assert.Empty((await GetAsync(other, "/v1.0/smartFolders")).GetProperty("value").EnumerateArray());
        using var intrude = await other.PostAsJsonAsync("/v1.0/smartFolders", new { name = "X", workspaceId = s.Workspace, definition = new { } }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, intrude.StatusCode);
    }
}
