using System.Net;
using System.Text;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>The term store (TAX): groups, sets, hierarchical terms, keywords, merges, promotion and CSV import.</summary>
public sealed class TaxonomyTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>A closed "Departments" term set: Finance → Accounting → Payables, and Sales.</summary>
    private static async Task<(string Set, Dictionary<string, string> Terms)> SetupAsync(HttpClient client)
    {
        var group = await PostIdAsync(client, "/v1.0/termStore/groups", new { name = "Organization" });
        var set = await PostIdAsync(client, "/v1.0/termStore/sets", new { groupId = group, name = "Departments" });
        var terms = new Dictionary<string, string>
        {
            ["finance"] = await PostIdAsync(client, $"/v1.0/termStore/sets/{set}/terms", new { name = "Finance", color = "#1F77B4" }),
        };
        terms["accounting"] = await PostIdAsync(client, $"/v1.0/termStore/sets/{set}/terms", new { name = "Accounting", parentId = terms["finance"], synonyms = new[] { "Bookkeeping" } });
        terms["payables"] = await PostIdAsync(client, $"/v1.0/termStore/sets/{set}/terms", new { name = "Payables", parentId = terms["accounting"] });
        terms["sales"] = await PostIdAsync(client, $"/v1.0/termStore/sets/{set}/terms", new { name = "Sales", labels = new[] { new { language = "de", name = "Vertrieb" } } });
        return (set, terms);
    }

    private static async Task<string> PostIdAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url) => await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);

    private static async Task<List<string>> TermNamesAsync(HttpClient client, string url) =>
        [.. (await GetAsync(client, url)).GetProperty("value").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal)];

    /// <summary>All active terms of a set (the API lists one level at a time).</summary>
    private static async Task<List<JsonElement>> AllTermsAsync(HttpClient client, string set, string? parent = null)
    {
        var level = (await GetAsync(client, $"/v1.0/termStore/sets/{set}/terms" + (parent is null ? "" : $"?parentId={parent}"))).GetProperty("value").EnumerateArray().ToList();
        var all = new List<JsonElement>(level);
        foreach (var term in level)
        {
            all.AddRange(await AllTermsAsync(client, set, term.Id()));
        }

        return all;
    }

    [Fact]
    public async Task Term_store_has_a_system_keywords_set()
    {
        var sets = (await GetAsync(_client, "/v1.0/termStore/sets")).GetProperty("value").EnumerateArray().ToList();
        var keywords = Assert.Single(sets, s => s.GetProperty("isKeywords").GetBoolean());
        Assert.True(keywords.GetProperty("isOpen").GetBoolean());
        var url = $"/v1.0/termStore/sets/{keywords.Id()}";
        var etag = keywords.ETag();

        using var delete = await _client.SendAsync(Api.WithETag(HttpMethod.Delete, url, null, etag), Ct);
        using var close = await _client.SendAsync(Api.Patch(url, new { isOpen = false }, etag), Ct);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, close.StatusCode);
    }

    [Fact]
    public async Task Terms_form_a_hierarchy_and_can_be_searched()
    {
        var (set, terms) = await SetupAsync(_client);

        Assert.Equal(["Finance", "Sales"], await TermNamesAsync(_client, $"/v1.0/termStore/sets/{set}/terms"));
        Assert.Equal(["Accounting"], await TermNamesAsync(_client, $"/v1.0/termStore/sets/{set}/terms?parentId={terms["finance"]}"));
        Assert.Equal(["Accounting"], await TermNamesAsync(_client, $"/v1.0/termStore/sets/{set}/terms?search=bookkeep"));
        Assert.Equal(["Sales"], await TermNamesAsync(_client, $"/v1.0/termStore/sets/{set}/terms?search=VERTRIEB"));

        var finance = await GetAsync(_client, $"/v1.0/termStore/sets/{set}/terms/{terms["finance"]}");
        Assert.True(finance.GetProperty("hasChildren").GetBoolean());
        Assert.Equal("#1f77b4", finance.GetProperty("color").GetString());

        using var duplicate = await _client.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms", new { name = "finance" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Terms_can_be_moved_but_not_under_their_descendants()
    {
        var (set, terms) = await SetupAsync(_client);
        var url = $"/v1.0/termStore/sets/{set}/terms/{terms["finance"]}";
        var etag = (await GetAsync(_client, url)).ETag();

        using var cycle = await _client.SendAsync(Api.Patch(url, new { parentId = terms["payables"] }, etag), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);

        var accountingUrl = $"/v1.0/termStore/sets/{set}/terms/{terms["accounting"]}";
        var accountingEtag = (await GetAsync(_client, accountingUrl)).ETag();
        using var moved = await _client.SendAsync(Api.Patch(accountingUrl, new { parentId = terms["sales"] }, accountingEtag), Ct);
        await moved.JsonAsync(HttpStatusCode.OK);
        Assert.Equal(["Accounting"], await TermNamesAsync(_client, $"/v1.0/termStore/sets/{set}/terms?parentId={terms["sales"]}"));
        Assert.Empty(await TermNamesAsync(_client, $"/v1.0/termStore/sets/{set}/terms?parentId={terms["finance"]}"));

        // Payables moved with its parent: still found under Accounting.
        Assert.Equal(["Payables"], await TermNamesAsync(_client, $"/v1.0/termStore/sets/{set}/terms?parentId={terms["accounting"]}"));
    }

    [Fact]
    public async Task Members_can_add_keywords_but_not_manage_closed_sets()
    {
        var (set, _) = await SetupAsync(_client);
        using (var user = await _client.PostAsJsonAsync("/v1.0/users", new { userName = "tagger", password = "tagger-password-1" }, Ct))
        {
            await user.JsonAsync(HttpStatusCode.Created);
        }

        var member = await _host.SignInAsync("tagger", "tagger-password-1");

        using var created = await member.PostAsJsonAsync("/v1.0/termStore/keywords", new { name = "Urgent" }, Ct);
        using var existing = await member.PostAsJsonAsync("/v1.0/termStore/keywords", new { name = "urgent " }, Ct);
        using var closed = await member.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms", new { name = "Legal" }, Ct);
        using var group = await member.PostAsJsonAsync("/v1.0/termStore/groups", new { name = "Mine" }, Ct);

        var id = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        Assert.Equal(id, (await existing.JsonAsync(HttpStatusCode.OK)).Id());
        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, group.StatusCode);

        var suggestions = (await GetAsync(member, "/v1.0/termStore/keywords?search=urg")).EnumerateArray().ToList();
        Assert.Equal("Urgent", Assert.Single(suggestions).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Merged_terms_move_their_children_and_become_synonyms()
    {
        var (set, terms) = await SetupAsync(_client);

        using var merge = await _client.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms/{terms["accounting"]}/merge", new { targetTermId = terms["sales"] }, Ct);
        var target = await merge.JsonAsync(HttpStatusCode.OK);
        Assert.Contains("Accounting", target.GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()));

        // Children move to the target; the source is no longer listed but found by id as merged.
        Assert.Equal(["Payables"], await TermNamesAsync(_client, $"/v1.0/termStore/sets/{set}/terms?parentId={terms["sales"]}"));
        Assert.Equal(["Finance", "Sales"], await TermNamesAsync(_client, $"/v1.0/termStore/sets/{set}/terms"));
        var source = await GetAsync(_client, $"/v1.0/termStore/sets/{set}/terms/{terms["accounting"]}");
        Assert.Equal(terms["sales"], source.GetProperty("mergedIntoId").GetString());

        using var again = await _client.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms/{terms["accounting"]}/merge", new { targetTermId = terms["sales"] }, Ct);
        using var intoChild = await _client.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms/{terms["sales"]}/merge", new { targetTermId = terms["payables"] }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, intoChild.StatusCode);
    }

    [Fact]
    public async Task Terms_are_found_by_id_across_sets()
    {
        var (_, terms) = await SetupAsync(_client);
        var keyword = await PostIdAsync(_client, "/v1.0/termStore/keywords", new { name = "Urgent" });

        var found = (await GetAsync(_client, $"/v1.0/termStore/terms?ids={terms["payables"]},{keyword},{Guid.NewGuid()}"))
            .EnumerateArray().ToDictionary(t => t.Id(), t => t.GetProperty("name").GetString());

        Assert.Equal(2, found.Count);
        Assert.Equal("Payables", found[terms["payables"]]);
        Assert.Equal("Urgent", found[keyword]);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/v1.0/termStore/terms?ids=nope", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/v1.0/termStore/terms", Ct)).StatusCode);
        var tooMany = string.Join(',', Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/v1.0/termStore/terms?ids={tooMany}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Keywords_are_promoted_into_a_term_set()
    {
        var group = await PostIdAsync(_client, "/v1.0/termStore/groups", new { name = "Knowledge" });
        var topics = await PostIdAsync(_client, "/v1.0/termStore/sets", new { groupId = group, name = "Topics" });
        var existingBeta = await PostIdAsync(_client, $"/v1.0/termStore/sets/{topics}/terms", new { name = "Beta" });
        var alpha = await PostIdAsync(_client, "/v1.0/termStore/keywords", new { name = "alpha" });
        var beta = await PostIdAsync(_client, "/v1.0/termStore/keywords", new { name = "beta" });

        // Moved: same id, now in Topics.
        using var move = await _client.PostAsJsonAsync($"/v1.0/termStore/keywords/{alpha}/promote", new { termSetId = topics }, Ct);
        var moved = await move.JsonAsync(HttpStatusCode.OK);
        Assert.False(moved.GetProperty("merged").GetBoolean());
        Assert.Equal(alpha, moved.GetProperty("termId").GetString());
        Assert.Equal(["Beta", "alpha"], await TermNamesAsync(_client, $"/v1.0/termStore/sets/{topics}/terms"));

        // A term of that name exists: the keyword is merged into it.
        using var merge = await _client.PostAsJsonAsync($"/v1.0/termStore/keywords/{beta}/promote", new { termSetId = topics }, Ct);
        var merged = await merge.JsonAsync(HttpStatusCode.OK);
        Assert.True(merged.GetProperty("merged").GetBoolean());
        Assert.Equal(existingBeta, merged.GetProperty("termId").GetString());

        using var notKeyword = await _client.PostAsJsonAsync($"/v1.0/termStore/keywords/{existingBeta}/promote", new { termSetId = topics }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, notKeyword.StatusCode);
    }

    [Fact]
    public async Task Term_sets_are_imported_from_sharepoint_csv()
    {
        var group = await PostIdAsync(_client, "/v1.0/termStore/groups", new { name = "Geography" });
        const string regions = """
            "Term Set Name","Term Set Description","LCID","Available for Tagging","Term Description","Level 1 Term","Level 2 Term","Level 3 Term"
            "Regions","Where we work",,TRUE,,"Europe","Germany","Berlin"
            ,,,TRUE,,"Europe","France",
            ,,,TRUE,"The Americas","America",,
            """;
        async Task<JsonElement> ImportAsync(HttpClient client, string csv, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await client.PostAsync($"/v1.0/termStore/groups/{group}/import", new StringContent(csv, Encoding.UTF8, "text/csv"), Ct);
            return await response.JsonAsync(expected);
        }

        var first = await ImportAsync(_client, regions);
        Assert.True(first.GetProperty("created").GetBoolean());
        Assert.Equal(5, first.GetProperty("termsCreated").GetInt32());
        var set = first.GetProperty("termSetId").GetString()!;
        var terms = await AllTermsAsync(_client, set);
        Assert.Equal(5, terms.Count);
        var berlin = terms.Single(t => t.GetProperty("name").GetString() == "Berlin");
        var germany = terms.Single(t => t.GetProperty("name").GetString() == "Germany");
        Assert.Equal(germany.Id(), berlin.GetProperty("parentId").GetString());
        Assert.Equal("The Americas", terms.Single(t => t.GetProperty("name").GetString() == "America").GetProperty("description").GetString());

        // Importing again only adds what is missing.
        var again = await ImportAsync(_client, regions + "\n,,,TRUE,,\"Europe\",\"Germany\",\"Munich\"\n");
        Assert.False(again.GetProperty("created").GetBoolean());
        Assert.Equal(1, again.GetProperty("termsCreated").GetInt32());

        await ImportAsync(_client, "just,some,columns\n1,2,3", HttpStatusCode.BadRequest);

        // Another tenant cannot import into this group.
        var other = await _host.CreateTenantAsync("tax-import-b");
        await ImportAsync(other, regions, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Term_store_is_isolated_per_tenant()
    {
        var (set, terms) = await SetupAsync(_client);
        var other = await _host.CreateTenantAsync("tax-isolation-b");

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/termStore/sets/{set}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/termStore/sets/{set}/terms/{terms["finance"]}", Ct)).StatusCode);
        Assert.Empty((await GetAsync(other, $"/v1.0/termStore/terms?ids={terms["finance"]}")).EnumerateArray());
        var groups = (await GetAsync(other, "/v1.0/termStore/groups")).GetProperty("value").EnumerateArray().ToList();
        Assert.DoesNotContain(groups, g => g.GetProperty("name").GetString() == "Organization");

        await PostIdAsync(_client, "/v1.0/termStore/keywords", new { name = "Secret" });
        Assert.Empty((await GetAsync(other, "/v1.0/termStore/keywords?search=secret")).EnumerateArray());
        using var term = await other.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms", new { name = "Mine" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, term.StatusCode);
    }
}
