using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

public sealed class TaxonomyTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh tenant with a closed "Departments" term set: Finance → Accounting → Payables, and Sales.</summary>
    private async Task<(HttpClient Client, Guid Set, Dictionary<string, Guid> Terms)> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var client = await ApiClient.CreateAsync(factory, tenant);
        var group = await PostIdAsync(client, "/v1.0/termStore/groups", new { name = "Organization" });
        var set = await PostIdAsync(client, "/v1.0/termStore/sets", new { groupId = group, name = "Departments" });
        var terms = new Dictionary<string, Guid>();
        terms["finance"] = await PostIdAsync(client, $"/v1.0/termStore/sets/{set}/terms", new { name = "Finance", color = "#1F77B4" });
        terms["accounting"] = await PostIdAsync(client, $"/v1.0/termStore/sets/{set}/terms", new { name = "Accounting", parentId = terms["finance"], synonyms = new[] { "Bookkeeping" } });
        terms["payables"] = await PostIdAsync(client, $"/v1.0/termStore/sets/{set}/terms", new { name = "Payables", parentId = terms["accounting"] });
        terms["sales"] = await PostIdAsync(client, $"/v1.0/termStore/sets/{set}/terms", new { name = "Sales", labels = new[] { new { language = "de", name = "Vertrieb" } } });
        return (client, set, terms);
    }

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        }

        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<List<string>> TermNamesAsync(HttpClient client, string url) =>
        (await (await client.GetAsync(url, Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Term_store_has_a_system_keywords_set()
    {
        await factory.CreateTenantAsync("tax-keywords-set");
        var client = await ApiClient.CreateAsync(factory, "tax-keywords-set");

        var sets = (await (await client.GetAsync("/v1.0/termStore/sets", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        var keywords = Assert.Single(sets, s => s.GetProperty("isKeywords").GetBoolean());
        Assert.True(keywords.GetProperty("isOpen").GetBoolean());
        var url = $"/v1.0/termStore/sets/{keywords.GetProperty("id").GetGuid()}";
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendWithEtagAsync(HttpMethod.Delete, url, etag)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { isOpen = false })).StatusCode);
    }

    [Fact]
    public async Task Terms_form_a_hierarchy_and_can_be_searched()
    {
        var (client, set, terms) = await SetupAsync("tax-hierarchy");

        Assert.Equal(["Finance", "Sales"], await TermNamesAsync(client, $"/v1.0/termStore/sets/{set}/terms"));
        Assert.Equal(["Accounting"], await TermNamesAsync(client, $"/v1.0/termStore/sets/{set}/terms?parentId={terms["finance"]}"));
        Assert.Equal(["Accounting"], await TermNamesAsync(client, $"/v1.0/termStore/sets/{set}/terms?search=bookkeep"));
        Assert.Equal(["Sales"], await TermNamesAsync(client, $"/v1.0/termStore/sets/{set}/terms?search=VERTRIEB"));

        var finance = await (await client.GetAsync($"/v1.0/termStore/sets/{set}/terms/{terms["finance"]}", Ct)).ReadJsonAsync();
        Assert.True(finance.GetProperty("hasChildren").GetBoolean());
        Assert.Equal("#1f77b4", finance.GetProperty("color").GetString());

        var duplicate = await client.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms", new { name = "finance" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Terms_can_be_moved_but_not_under_their_descendants()
    {
        var (client, set, terms) = await SetupAsync("tax-move");
        var url = $"/v1.0/termStore/sets/{set}/terms/{terms["finance"]}";
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;

        var cycle = await client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { parentId = terms["payables"] });
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);

        var accountingUrl = $"/v1.0/termStore/sets/{set}/terms/{terms["accounting"]}";
        var accountingEtag = (await client.GetAsync(accountingUrl, Ct)).Headers.ETag!.Tag;
        var moved = await client.SendWithEtagAsync(HttpMethod.Patch, accountingUrl, accountingEtag, new { parentId = terms["sales"] });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(["Accounting"], await TermNamesAsync(client, $"/v1.0/termStore/sets/{set}/terms?parentId={terms["sales"]}"));
        Assert.Empty(await TermNamesAsync(client, $"/v1.0/termStore/sets/{set}/terms?parentId={terms["finance"]}"));
    }

    [Fact]
    public async Task Members_can_add_keywords_but_not_manage_closed_sets()
    {
        var (admin, set, _) = await SetupAsync("tax-members");
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "tagger", password = "tagger-password-1" }, Ct);
        var member = await ApiClient.CreateAsync(factory, "tax-members", "tagger", "tagger-password-1");

        var created = await member.PostAsJsonAsync("/v1.0/termStore/keywords", new { name = "Urgent" }, Ct);
        var existing = await member.PostAsJsonAsync("/v1.0/termStore/keywords", new { name = "urgent " }, Ct);
        var closed = await member.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms", new { name = "Legal" }, Ct);
        var group = await member.PostAsJsonAsync("/v1.0/termStore/groups", new { name = "Mine" }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        Assert.Equal((await created.ReadJsonAsync()).GetProperty("id").GetGuid(), (await existing.ReadJsonAsync()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, group.StatusCode);

        var suggestions = (await (await member.GetAsync("/v1.0/termStore/keywords?search=urg", Ct)).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal("Urgent", Assert.Single(suggestions).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Managed_metadata_fields_resolve_terms_and_filter_hierarchically()
    {
        var (client, set, terms) = await SetupAsync("tax-fields");
        var ws = await client.CreateWorkspaceAsync("Docs");
        var contentType = await client.CreateContentTypeAsync("Document",
        [
            new { name = "department", type = "managedMetadata", termSetId = set },
            new { name = "keywords", type = "keywords", allowMultiple = true },
        ]);
        var list = await client.CreateListAsync(ws, "Documents", contentType);

        var byLabel = await client.CreateItemAsync(ws, list, new { fields = new { title = "Invoice", department = "payables", keywords = new[] { "Tax", "tax", "2026" } } });
        await client.CreateItemAsync(ws, list, new { fields = new { title = "Ledger", department = terms["accounting"].ToString() } });
        await client.CreateItemAsync(ws, list, new { fields = new { title = "Pitch", department = terms["sales"].ToString(), keywords = new[] { "tax" } } });
        var unknown = await client.PostItemAsync(ws, list, new { fields = new { title = "X", department = "Legal" } });
        var otherSet = await client.PostItemAsync(ws, list, new { fields = new { title = "Y", department = Guid.NewGuid().ToString() } });

        Assert.Equal(terms["payables"].ToString(), byLabel.GetProperty("fields").GetProperty("department").GetString());
        Assert.Equal(2, byLabel.GetProperty("fields").GetProperty("keywords").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otherSet.StatusCode);

        // Filtering on a term matches its descendants.
        Assert.Equal(["Invoice", "Ledger"], (await client.QueryTitlesAsync(ws, list, $"$filter=fields/department eq {terms["finance"]}")).Order().ToList());
        Assert.Equal(["Invoice"], await client.QueryTitlesAsync(ws, list, $"$filter=fields/department eq {terms["payables"]}"));
        Assert.Equal(["Pitch"], await client.QueryTitlesAsync(ws, list, $"$filter=fields/department ne {terms["finance"]}"));

        var tax = (await (await client.GetAsync("/v1.0/termStore/keywords?search=tax", Ct)).ReadJsonAsync()).EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Equal(["Invoice", "Pitch"], (await client.QueryTitlesAsync(ws, list, $"$filter=fields/keywords/any(k: k eq {tax})")).Order().ToList());
    }

    [Fact]
    public async Task Term_sets_of_managed_metadata_fields_must_exist()
    {
        await factory.CreateTenantAsync("tax-field-validation");
        var client = await ApiClient.CreateAsync(factory, "tax-field-validation");

        var missing = await client.PostAsJsonAsync("/v1.0/contentTypes", new { name = "A", fields = new object[] { new { name = "d", type = "managedMetadata" } } }, Ct);
        var unknown = await client.PostAsJsonAsync("/v1.0/contentTypes", new { name = "B", fields = new object[] { new { name = "d", type = "managedMetadata", termSetId = Guid.NewGuid() } } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Merging_terms_rewrites_item_values_in_the_background()
    {
        var (client, set, terms) = await SetupAsync("tax-merge");
        var ws = await client.CreateWorkspaceAsync("Docs");
        var contentType = await client.CreateContentTypeAsync("Document",
            [new { name = "departments", type = "managedMetadata", termSetId = set, allowMultiple = true }]);
        var list = await client.CreateListAsync(ws, "Documents", contentType);
        var item = await client.CreateItemAsync(ws, list, new { fields = new { title = "Both", departments = new[] { "Sales", "Accounting" } } });
        var itemUrl = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item.GetProperty("id").GetGuid()}";

        var merge = await client.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms/{terms["accounting"]}/merge", new { targetTermId = terms["sales"] }, Ct);
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
        var target = await merge.ReadJsonAsync();
        Assert.Contains("Accounting", target.GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()));

        // Children move to the target; the source is no longer listed but still resolves.
        Assert.Equal(["Payables"], await TermNamesAsync(client, $"/v1.0/termStore/sets/{set}/terms?parentId={terms["sales"]}"));
        Assert.Equal(["Finance", "Sales"], await TermNamesAsync(client, $"/v1.0/termStore/sets/{set}/terms"));
        var viaOldId = await client.CreateItemAsync(ws, list, new { fields = new { title = "Old id", departments = new[] { terms["accounting"].ToString() } } });
        Assert.Equal(terms["sales"].ToString(), viaOldId.GetProperty("fields").GetProperty("departments")[0].GetString());

        var values = await Eventually.WaitForAsync<JsonElement>(async () =>
        {
            var fields = (await (await client.GetAsync(itemUrl, Ct)).ReadJsonAsync()).GetProperty("fields").GetProperty("departments");
            return fields.GetArrayLength() == 1 ? fields : (JsonElement?)null;
        });
        Assert.Equal(terms["sales"].ToString(), values[0].GetString());
    }

    [Fact]
    public async Task Term_store_is_isolated_per_tenant()
    {
        var (clientA, setA, termsA) = await SetupAsync("tax-isolation-a");
        await factory.CreateTenantAsync("tax-isolation-b");
        var clientB = await ApiClient.CreateAsync(factory, "tax-isolation-b");

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/v1.0/termStore/sets/{setA}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/v1.0/termStore/sets/{setA}/terms/{termsA["finance"]}", Ct)).StatusCode);
        var groupsB = (await (await clientB.GetAsync("/v1.0/termStore/groups", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        Assert.DoesNotContain(groupsB, g => g.GetProperty("name").GetString() == "Organization");

        await clientA.PostAsJsonAsync("/v1.0/termStore/keywords", new { name = "Secret" }, Ct);
        Assert.Empty((await (await clientB.GetAsync("/v1.0/termStore/keywords?search=secret", Ct)).ReadJsonAsync()).EnumerateArray());

        var wsB = await clientB.CreateWorkspaceAsync("Docs");
        var foreign = await clientB.PostAsJsonAsync("/v1.0/contentTypes",
            new { name = "X", fields = new object[] { new { name = "d", type = "managedMetadata", termSetId = setA } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.NotEqual(Guid.Empty, wsB);
    }
}
