using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Unified search (SRC-01…04). Indexing is asynchronous, so assertions wait for it.</summary>
public sealed class SearchTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(HttpClient Admin, string Tenant, Guid Workspace, Guid List, Guid ContentType, Guid TermSet, Dictionary<string, Guid> Terms);

    private async Task<Setup> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var admin = await ApiClient.CreateAsync(factory, tenant);
        var group = (await (await admin.PostAsJsonAsync("/v1.0/termStore/groups", new { name = "Org" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var set = (await (await admin.PostAsJsonAsync("/v1.0/termStore/sets", new { groupId = group, name = "Topics" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var terms = new Dictionary<string, Guid>();
        terms["finance"] = (await (await admin.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms", new { name = "Finance" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        terms["tax"] = (await (await admin.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms", new { name = "Tax", parentId = terms["finance"], synonyms = new[] { "Levy" } }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();

        var ws = await admin.CreateWorkspaceAsync("Office");
        var contentType = await admin.CreateContentTypeAsync("Record",
        [
            new { name = "summary", type = "note" },
            new { name = "status", type = "choice", choices = new[] { "open", "paid" } },
            new { name = "topic", type = "managedMetadata", termSetId = set },
        ]);
        var list = await admin.CreateListAsync(ws, "Records", contentType);
        return new Setup(admin, tenant, ws, list, contentType, set, terms);
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/v1.0/search?{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static List<string> Titles(JsonElement result) =>
        result.GetProperty("value").EnumerateArray().Select(h => h.GetProperty("title").GetString()!).Order(StringComparer.Ordinal).ToList();

    /// <summary>Waits until the search returns exactly <paramref name="expected"/> titles.</summary>
    private static async Task<JsonElement> WaitForAsync(HttpClient client, string query, params string[] expected)
    {
        JsonElement last = default;
        try
        {
            await Eventually.WaitForAsync<bool>(async () =>
            {
                last = await SearchAsync(client, query);
                return Titles(last).SequenceEqual(expected.Order(StringComparer.Ordinal)) ? true : null;
            });
        }
        catch (TimeoutException)
        {
            Assert.Fail($"'{query}' returned [{string.Join(", ", Titles(last))}], expected [{string.Join(", ", expected)}].");
        }

        return last;
    }

    [Fact]
    public async Task Items_are_found_by_words_phrases_prefixes_and_tags()
    {
        var s = await SetupAsync("search-basic");
        await s.Admin.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Invoice INV-2026-7", summary = "Office chairs, due date in March", status = "open", topic = "Tax" } });
        await s.Admin.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Invoice INV-2026-8", summary = "Printer paper", status = "paid", topic = "Finance" } });
        await s.Admin.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Meeting notes", summary = "Discussed the invoice backlog and chairs" } });

        await WaitForAsync(s.Admin, "q=invoice", "Invoice INV-2026-7", "Invoice INV-2026-8", "Meeting notes");
        await WaitForAsync(s.Admin, "q=\"due date\"", "Invoice INV-2026-7");
        await WaitForAsync(s.Admin, "q=chairs -meeting", "Invoice INV-2026-7");
        await WaitForAsync(s.Admin, "q=paper OR backlog", "Invoice INV-2026-8", "Meeting notes");
        await WaitForAsync(s.Admin, "q=print*", "Invoice INV-2026-8");
        await WaitForAsync(s.Admin, "q=inv-2026-8", "Invoice INV-2026-8");
        await WaitForAsync(s.Admin, "q=levy", "Invoice INV-2026-7");

        // Title matches rank above body matches.
        var ranked = await SearchAsync(s.Admin, "q=invoice");
        Assert.Equal("Meeting notes", ranked.GetProperty("value")[2].GetProperty("title").GetString());
        Assert.Contains("invoice", ranked.GetProperty("value")[2].GetProperty("snippet").GetString()!, StringComparison.OrdinalIgnoreCase);

        // Hierarchical tag filter and facets.
        var finance = await WaitForAsync(s.Admin, $"termId={s.Terms["finance"]}", "Invoice INV-2026-7", "Invoice INV-2026-8");
        Assert.Equal(2, finance.GetProperty("@odata.count").GetInt32());
        var termFacet = finance.GetProperty("facets").GetProperty("term").EnumerateArray().ToDictionary(f => f.GetProperty("value").GetGuid(), f => f.GetProperty("count").GetInt32());
        Assert.Equal(1, termFacet[s.Terms["tax"]]);
        Assert.Equal(2, finance.GetProperty("facets").GetProperty("container")[0].GetProperty("count").GetInt32());

        // Paging.
        var first = await SearchAsync(s.Admin, "q=invoice&$top=2");
        Assert.Equal(2, first.GetProperty("value").GetArrayLength());
        var next = await (await s.Admin.GetAsync(first.GetProperty("@odata.nextLink").GetString(), Ct)).ReadJsonAsync();
        Assert.Equal(1, next.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Search_only_returns_what_the_caller_may_read()
    {
        var s = await SetupAsync("search-trimming");
        await s.Admin.PostAsJsonAsync("/v1.0/users", new { userName = "alice", password = "alice-password-1" }, Ct);
        await s.Admin.PostAsJsonAsync("/v1.0/users", new { userName = "mallory", password = "mallory-password-1" }, Ct);
        var users = (await (await s.Admin.GetAsync("/v1.0/users", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .ToDictionary(u => u.GetProperty("userName").GetString()!, u => u.GetProperty("id").GetGuid());
        await s.Admin.PostAsJsonAsync($"/v1.0/workspaces/{s.Workspace}/members", new { userId = users["alice"], role = "member" }, Ct);
        var alice = await ApiClient.CreateAsync(factory, s.Tenant, "alice", "alice-password-1");
        var mallory = await ApiClient.CreateAsync(factory, s.Tenant, "mallory", "mallory-password-1");

        var folder = (await s.Admin.CreateItemAsync(s.Workspace, s.List, new { isFolder = true, fields = new { title = "Board" } })).GetProperty("id").GetGuid();
        await s.Admin.CreateItemAsync(s.Workspace, s.List, new { parentId = folder, fields = new { title = "Secret merger plan" } });
        await s.Admin.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Public merger FAQ" } });

        await WaitForAsync(alice, "q=merger", "Public merger FAQ", "Secret merger plan");
        await WaitForAsync(mallory, "q=merger");

        // Breaking inheritance on the folder re-indexes the list with the new principals.
        var listUrl = $"/v1.0/workspaces/{s.Workspace}/lists/{s.List}";
        await s.Admin.PostAsJsonAsync($"{listUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        await WaitForAsync(alice, "q=merger", "Public merger FAQ");
        await WaitForAsync(s.Admin, "q=merger", "Public merger FAQ", "Secret merger plan");

        // Deleted items disappear; restored ones come back.
        var faq = (await alice.GetAsync($"{listUrl}/items?$filter=fields/title eq 'Public merger FAQ'", Ct));
        var faqId = (await faq.ReadJsonAsync()).GetProperty("value")[0].GetProperty("id").GetGuid();
        var etag = (await alice.GetAsync($"{listUrl}/items/{faqId}", Ct)).Headers.ETag!.Tag;
        await alice.SendWithEtagAsync(HttpMethod.Delete, $"{listUrl}/items/{faqId}", etag);
        await WaitForAsync(alice, "q=merger");
        await alice.PostAsync($"{listUrl}/recycleBin/{faqId}/restore", null, Ct);
        await WaitForAsync(alice, "q=merger", "Public merger FAQ");

        // A deleted list takes its documents with it.
        var listEtag = (await s.Admin.GetAsync(listUrl, Ct)).Headers.ETag!.Tag;
        await s.Admin.SendWithEtagAsync(HttpMethod.Delete, listUrl, listEtag);
        await WaitForAsync(s.Admin, "q=merger");
    }

    [Fact]
    public async Task Invalid_queries_are_rejected()
    {
        var s = await SetupAsync("search-invalid");

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync("/v1.0/search?q=-draft", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync("/v1.0/search", Ct)).StatusCode);
    }

    [Fact]
    public async Task Reindex_rebuilds_the_index_and_needs_admin_rights()
    {
        var s = await SetupAsync("search-reindex");
        await s.Admin.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Quarterly report" } });
        await WaitForAsync(s.Admin, "q=quarterly", "Quarterly report");

        var started = await s.Admin.PostAsync("/v1.0/search/reindex", null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var operation = started.Headers.Location!.ToString();
        await Eventually.WaitForAsync<bool>(async () =>
            (await (await s.Admin.GetAsync(operation, Ct)).ReadJsonAsync()).GetProperty("status").GetString() is "succeeded" ? true : null);
        await WaitForAsync(s.Admin, "q=quarterly", "Quarterly report");

        await s.Admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct);
        var member = await ApiClient.CreateAsync(factory, s.Tenant, "member", "member-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/v1.0/search/reindex", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Search_is_isolated_per_tenant()
    {
        var a = await SetupAsync("search-tenant-a");
        await a.Admin.CreateItemAsync(a.Workspace, a.List, new { fields = new { title = "Tenant A confidential" } });
        await WaitForAsync(a.Admin, "q=confidential", "Tenant A confidential");

        var b = await SetupAsync("search-tenant-b");
        await b.Admin.CreateItemAsync(b.Workspace, b.List, new { fields = new { title = "Tenant B other" } });
        await WaitForAsync(b.Admin, "q=other", "Tenant B other");
        Assert.Empty(Titles(await SearchAsync(b.Admin, "q=confidential")));
    }
}
