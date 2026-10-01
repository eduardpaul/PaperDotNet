using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Unified search (SRC-01…04). Indexing is asynchronous, so assertions wait for it.</summary>
public sealed class SearchTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private sealed record Setup(string Workspace, string List, string TermSet, Dictionary<string, string> Terms);

    /// <summary>A workspace with a "Records" list (summary, status, topic from a Topics set: Finance → Tax).</summary>
    private static async Task<Setup> SetupAsync(HttpClient admin)
    {
        var group = await PostIdAsync(admin, "/v1.0/termStore/groups", new { name = "Org" });
        var set = await PostIdAsync(admin, "/v1.0/termStore/sets", new { groupId = group, name = "Topics" });
        var terms = new Dictionary<string, string> { ["finance"] = await PostIdAsync(admin, $"/v1.0/termStore/sets/{set}/terms", new { name = "Finance" }) };
        terms["tax"] = await PostIdAsync(admin, $"/v1.0/termStore/sets/{set}/terms", new { name = "Tax", parentId = terms["finance"], synonyms = new[] { "Levy" } });

        var ws = await Api.CreateWorkspaceAsync(admin, "Office");
        var list = (await Api.CreateListAsync(admin, ws, "Records", new object[]
        {
            new { name = "summary", displayName = "Summary", type = "note" },
            new { name = "status", displayName = "Status", type = "choice", choices = new[] { "open", "paid" } },
            new { name = "topic", displayName = "Topic", type = "managedMetadata", termSetId = set },
        })).Id();
        return new Setup(ws, list, set, terms);
    }

    private static async Task<string> PostIdAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync($"/v1.0/search?{query}", Ct);
        return await response.JsonAsync(HttpStatusCode.OK);
    }

    private static List<string> Titles(JsonElement result) =>
        [.. result.GetProperty("value").EnumerateArray().Select(h => h.GetProperty("title").GetString()!).Order(StringComparer.Ordinal)];

    /// <summary>Waits until the search returns exactly <paramref name="expected"/> titles.</summary>
    private static async Task<JsonElement> WaitForAsync(HttpClient client, string query, params string[] expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var result = await SearchAsync(client, query);
            if (Titles(result).SequenceEqual(expected.Order(StringComparer.Ordinal)))
            {
                return result;
            }

            Assert.True(DateTime.UtcNow < deadline, $"'{query}' returned [{string.Join(", ", Titles(result))}], expected [{string.Join(", ", expected)}].");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Items_are_found_by_words_phrases_prefixes_and_tags()
    {
        var s = await SetupAsync(_admin);
        await Api.CreateItemAsync(_admin, s.Workspace, s.List, new { title = "Invoice INV-2026-7", summary = "Office chairs, due date in March", status = "open", topic = "Tax" });
        await Api.CreateItemAsync(_admin, s.Workspace, s.List, new { title = "Invoice INV-2026-8", summary = "Printer paper", status = "paid", topic = "Finance" });
        await Api.CreateItemAsync(_admin, s.Workspace, s.List, new { title = "Meeting notes", summary = "Discussed the invoice backlog and chairs" });

        await WaitForAsync(_admin, "q=invoice", "Invoice INV-2026-7", "Invoice INV-2026-8", "Meeting notes");
        await WaitForAsync(_admin, "q=\"due date\"", "Invoice INV-2026-7");
        await WaitForAsync(_admin, "q=chairs -meeting", "Invoice INV-2026-7");
        await WaitForAsync(_admin, "q=paper OR backlog", "Invoice INV-2026-8", "Meeting notes");
        await WaitForAsync(_admin, "q=print*", "Invoice INV-2026-8");
        await WaitForAsync(_admin, "q=inv-2026-8", "Invoice INV-2026-8");
        await WaitForAsync(_admin, "q=levy", "Invoice INV-2026-7");

        // Title matches rank above body matches; the snippet shows the match.
        var ranked = await SearchAsync(_admin, "q=invoice");
        var last = ranked.GetProperty("value")[2];
        Assert.Equal("Meeting notes", last.GetProperty("title").GetString());
        Assert.Contains("invoice", last.GetProperty("snippet").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keyword", ranked.GetProperty("mode").GetString());

        // Hierarchical tag filter and facets.
        var finance = await WaitForAsync(_admin, $"termId={s.Terms["finance"]}", "Invoice INV-2026-7", "Invoice INV-2026-8");
        Assert.Equal(2, finance.GetProperty("@odata.count").GetInt32());
        var termFacet = finance.GetProperty("facets").GetProperty("term").EnumerateArray().ToDictionary(f => f.GetProperty("value").GetString()!, f => f.GetProperty("count").GetInt32());
        Assert.Equal(1, termFacet[s.Terms["tax"]]);
        var container = Assert.Single(finance.GetProperty("facets").GetProperty("container").EnumerateArray());
        Assert.Equal(2, container.GetProperty("count").GetInt32());
        Assert.Equal(s.List, container.GetProperty("value").GetString());

        // Paging.
        var first = await SearchAsync(_admin, "q=invoice&$top=2");
        Assert.Equal(2, first.GetProperty("value").GetArrayLength());
        Assert.Equal(3, first.GetProperty("@odata.count").GetInt32());
        using var nextResponse = await _admin.GetAsync(first.GetProperty("@odata.nextLink").GetString(), Ct);
        Assert.Equal(1, (await nextResponse.JsonAsync(HttpStatusCode.OK)).GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Search_only_returns_what_the_caller_may_read()
    {
        var s = await SetupAsync(_admin);
        var users = new Dictionary<string, string>();
        foreach (var name in new[] { "alice", "mallory" })
        {
            users[name] = await PostIdAsync(_admin, "/v1.0/users", new { userName = name, password = $"{name}-password-1" });
        }

        using (var member = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{s.Workspace}/members", new { userId = users["alice"], role = "member" }, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, member.StatusCode);
        }

        var alice = await _host.SignInAsync("alice", "alice-password-1");
        var mallory = await _host.SignInAsync("mallory", "mallory-password-1");
        var listUrl = $"/v1.0/workspaces/{s.Workspace}/lists/{s.List}";
        using var folderResponse = await _admin.PostAsJsonAsync($"{listUrl}/items", new { isFolder = true, fields = new { title = "Board" } }, Ct);
        var folder = (await folderResponse.JsonAsync(HttpStatusCode.Created)).Id();
        using (var secret = await _admin.PostAsJsonAsync($"{listUrl}/items", new { parentId = folder, fields = new { title = "Secret merger plan" } }, Ct))
        {
            await secret.JsonAsync(HttpStatusCode.Created);
        }

        var faq = (await Api.CreateItemAsync(_admin, s.Workspace, s.List, new { title = "Public merger FAQ" })).Id();

        await WaitForAsync(alice, "q=merger", "Public merger FAQ", "Secret merger plan");
        await WaitForAsync(mallory, "q=merger");

        // Breaking inheritance on the folder moves its documents to the folder's scope (ADR-0035).
        using (var broken = await _admin.PostAsJsonAsync($"{listUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct))
        {
            await broken.JsonAsync(HttpStatusCode.OK);
        }

        await WaitForAsync(alice, "q=merger", "Public merger FAQ");
        await WaitForAsync(_admin, "q=merger", "Public merger FAQ", "Secret merger plan");

        // Deleted items disappear; restored ones come back.
        var etag = (await (await alice.GetAsync($"{listUrl}/items/{faq}", Ct)).JsonAsync(HttpStatusCode.OK)).ETag();
        using (var deleted = await alice.SendAsync(Api.WithETag(HttpMethod.Delete, $"{listUrl}/items/{faq}", null, etag), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await WaitForAsync(alice, "q=merger");
        using (var restored = await alice.PostAsync($"{listUrl}/recycleBin/{faq}/restore", null, Ct))
        {
            Assert.True(restored.IsSuccessStatusCode, restored.StatusCode.ToString());
        }

        await WaitForAsync(alice, "q=merger", "Public merger FAQ");

        // A deleted list takes its documents with it.
        var listEtag = (await (await _admin.GetAsync(listUrl, Ct)).JsonAsync(HttpStatusCode.OK)).ETag();
        using (var deletedList = await _admin.SendAsync(Api.WithETag(HttpMethod.Delete, listUrl, null, listEtag), Ct))
        {
            Assert.True(deletedList.IsSuccessStatusCode, deletedList.StatusCode.ToString());
        }

        await WaitForAsync(_admin, "q=merger");
    }

    [Fact]
    public async Task Comments_are_part_of_the_item_document()
    {
        var s = await SetupAsync(_admin);
        var item = (await Api.CreateItemAsync(_admin, s.Workspace, s.List, new { title = "Supplier contract" })).Id();
        await WaitForAsync(_admin, "q=supplier", "Supplier contract");

        var comments = $"{Api.Items(s.Workspace, s.List)}/{item}/comments";
        using var created = await _admin.PostAsJsonAsync(comments, new { text = "Clause 7 mentions a penalty" }, Ct);
        var comment = await created.JsonAsync(HttpStatusCode.Created);
        await WaitForAsync(_admin, "q=penalty", "Supplier contract");

        using var deleted = await _admin.DeleteAsync($"{comments}/{comment.Id()}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await WaitForAsync(_admin, "q=penalty");
    }

    [Fact]
    public async Task Invalid_queries_are_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/v1.0/search?q=-draft", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/v1.0/search", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/v1.0/search?q=x&mode=semantic", Ct)).StatusCode);
    }

    [Fact]
    public async Task Reindex_rebuilds_the_index_and_needs_admin_rights()
    {
        var s = await SetupAsync(_admin);
        await Api.CreateItemAsync(_admin, s.Workspace, s.List, new { title = "Quarterly report" });
        await WaitForAsync(_admin, "q=quarterly", "Quarterly report");

        using var started = await _admin.PostAsync("/v1.0/search/reindex", null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var operation = started.Headers.Location!.ToString();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement finished;
        while ((finished = await (await _admin.GetAsync(operation, Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("status").GetString() != "succeeded")
        {
            Assert.True(DateTime.UtcNow < deadline, finished.ToString());
            await Task.Delay(100, Ct);
        }

        Assert.Equal(100, finished.GetProperty("percentComplete").GetInt32());
        Assert.Equal("listItem", finished.GetProperty("result").GetProperty("sources")[0].GetString());
        await WaitForAsync(_admin, "q=quarterly", "Quarterly report");

        await PostIdAsync(_admin, "/v1.0/users", new { userName = "member", password = "member-password-1" });
        var member = await _host.SignInAsync("member", "member-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/v1.0/search/reindex", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Search_is_isolated_per_tenant()
    {
        var a = await SetupAsync(_admin);
        await Api.CreateItemAsync(_admin, a.Workspace, a.List, new { title = "Tenant A confidential" });
        await WaitForAsync(_admin, "q=confidential", "Tenant A confidential");

        var other = await _host.CreateTenantAsync("search-b");
        var b = await SetupAsync(other);
        await Api.CreateItemAsync(other, b.Workspace, b.List, new { title = "Tenant B other" });
        await WaitForAsync(other, "q=other", "Tenant B other");
        Assert.Empty(Titles(await SearchAsync(other, "q=confidential")));
        Assert.Empty(Titles(await SearchAsync(other, $"termId={a.Terms["finance"]}")));
    }
}
