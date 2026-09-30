using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Promoting keywords (TAX-05), term set import and term sets from extensions (TAX-11).</summary>
public sealed class TaxonomyCurationTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    /// <summary>All active terms of a set (the API lists one level at a time).</summary>
    private static async Task<List<JsonElement>> AllTermsAsync(HttpClient client, Guid set, Guid? parent = null)
    {
        var url = $"/v1.0/termStore/sets/{set}/terms" + (parent is { } p ? $"?parentId={p}" : "");
        var level = (await (await client.GetAsync(url, Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        var all = new List<JsonElement>(level);
        foreach (var term in level)
        {
            all.AddRange(await AllTermsAsync(client, set, term.GetProperty("id").GetGuid()));
        }

        return all;
    }

    private static async Task<List<JsonElement>> PopularAsync(HttpClient client) =>
        (await (await client.GetAsync("/v1.0/termStore/keywords/popular", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();

    [Fact]
    public async Task Popular_keywords_are_promoted_into_a_term_set_without_breaking_items()
    {
        await factory.CreateTenantAsync("tax-promote");
        var client = await ApiClient.CreateAsync(factory, "tax-promote");
        var group = await PostIdAsync(client, "/v1.0/termStore/groups", new { name = "Knowledge" });
        var topics = await PostIdAsync(client, "/v1.0/termStore/sets", new { groupId = group, name = "Topics" });
        var existingBeta = await PostIdAsync(client, $"/v1.0/termStore/sets/{topics}/terms", new { name = "Beta" });

        var ws = await client.CreateWorkspaceAsync("Wiki");
        var contentType = await client.CreateContentTypeAsync("Page",
        [
            new { name = "keywords", type = "keywords", allowMultiple = true },
            new { name = "topic", type = "managedMetadata", termSetId = topics },
        ]);
        var list = await client.CreateListAsync(ws, "Pages", contentType);
        var first = await client.CreateItemAsync(ws, list, new { fields = new { title = "One", keywords = new[] { "alpha", "beta" } } });
        await client.CreateItemAsync(ws, list, new { fields = new { title = "Two", keywords = new[] { "alpha" } } });

        // Usage comes from the search index (filled asynchronously).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<JsonElement> popular;
        do
        {
            popular = await PopularAsync(client);
            await Task.Delay(200, Ct);
        }
        while (popular.FirstOrDefault().ValueKind == JsonValueKind.Undefined || popular[0].GetProperty("usage").GetInt32() < 2 && DateTime.UtcNow < deadline);
        Assert.Equal("alpha", popular[0].GetProperty("name").GetString());
        Assert.Equal(2, popular[0].GetProperty("usage").GetInt32());
        var alpha = popular[0].GetProperty("id").GetGuid();
        var beta = popular.Single(k => k.GetProperty("name").GetString() == "beta").GetProperty("id").GetGuid();

        // Moved: same id, now in Topics and still usable as a keyword.
        var moved = await (await client.PostAsJsonAsync($"/v1.0/termStore/keywords/{alpha}/promote", new { termSetId = topics }, Ct)).ReadJsonAsync();
        Assert.False(moved.GetProperty("merged").GetBoolean());
        Assert.Equal(alpha, moved.GetProperty("termId").GetGuid());
        Assert.Contains("alpha", (await (await client.GetAsync($"/v1.0/termStore/sets/{topics}/terms", Ct)).ReadJsonAsync())
            .GetProperty("value").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        var third = await client.CreateItemAsync(ws, list, new { fields = new { title = "Three", keywords = new[] { "Alpha" }, topic = "alpha" } });
        Assert.Equal(alpha.ToString(), third.GetProperty("fields").GetProperty("keywords")[0].GetString());
        Assert.Equal(alpha.ToString(), third.GetProperty("fields").GetProperty("topic").GetString());
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items/{first.GetProperty("id").GetGuid()}";
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields = new { title = "One (edited)" } })).StatusCode);

        // A term of that name exists: the keyword is merged into it and items are rewritten.
        var merged = await (await client.PostAsJsonAsync($"/v1.0/termStore/keywords/{beta}/promote", new { termSetId = topics }, Ct)).ReadJsonAsync();
        Assert.True(merged.GetProperty("merged").GetBoolean());
        Assert.Equal(existingBeta, merged.GetProperty("termId").GetGuid());
        deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await (await client.GetAsync(url, Ct)).ReadJsonAsync()).GetProperty("fields").GetProperty("keywords").EnumerateArray()
                   .All(k => k.GetString() != existingBeta.ToString()) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, Ct);
        }

        Assert.Contains(existingBeta.ToString(), (await (await client.GetAsync(url, Ct)).ReadJsonAsync()).GetProperty("fields").GetProperty("keywords").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/v1.0/termStore/keywords/{existingBeta}/promote", new { termSetId = topics }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Term_sets_are_imported_from_sharepoint_csv()
    {
        await factory.CreateTenantAsync("tax-import");
        var client = await ApiClient.CreateAsync(factory, "tax-import");
        var group = await PostIdAsync(client, "/v1.0/termStore/groups", new { name = "Geography" });
        const string Csv = """
            "Term Set Name","Term Set Description","LCID","Available for Tagging","Term Description","Level 1 Term","Level 2 Term","Level 3 Term"
            "Regions","Where we work",,TRUE,,"Europe","Germany","Berlin"
            ,,,TRUE,,"Europe","France",
            ,,,TRUE,"The Americas","America",,
            """;
        async Task<JsonElement> ImportAsync(string csv)
        {
            var response = await client.PostAsync($"/v1.0/termStore/groups/{group}/import", new StringContent(csv, Encoding.UTF8, "text/csv"), Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return await response.ReadJsonAsync();
        }

        var first = await ImportAsync(Csv);
        Assert.True(first.GetProperty("created").GetBoolean());
        Assert.Equal(5, first.GetProperty("termsCreated").GetInt32());
        var set = first.GetProperty("termSetId").GetGuid();
        var terms = await AllTermsAsync(client, set);
        Assert.Equal(5, terms.Count);
        var berlin = terms.Single(t => t.GetProperty("name").GetString() == "Berlin");
        var germany = terms.Single(t => t.GetProperty("name").GetString() == "Germany");
        Assert.Equal(germany.GetProperty("id").GetGuid(), berlin.GetProperty("parentId").GetGuid());

        // Importing again only adds what is missing.
        var again = await ImportAsync(Csv + "\n,,,TRUE,,\"Europe\",\"Germany\",\"Munich\"\n");
        Assert.False(again.GetProperty("created").GetBoolean());
        Assert.Equal(1, again.GetProperty("termsCreated").GetInt32());

        var invalid = await client.PostAsync($"/v1.0/termStore/groups/{group}/import", new StringContent("just,some,columns\n1,2,3", Encoding.UTF8, "text/csv"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // Another tenant cannot import into this group.
        await factory.CreateTenantAsync("tax-import-b");
        var other = await ApiClient.CreateAsync(factory, "tax-import-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/v1.0/termStore/groups/{group}/import", new StringContent(Csv, Encoding.UTF8, "text/csv"), Ct)).StatusCode);
        Assert.Empty(await PopularAsync(other));
    }

    [Fact]
    public async Task Extensions_provide_term_sets_when_enabled()
    {
        await factory.CreateTenantAsync("tax-extension");
        var client = await ApiClient.CreateAsync(factory, "tax-extension");
        static bool IsCostCenters(JsonElement s) => s.GetProperty("name").GetString() == "Cost centers";
        Assert.DoesNotContain((await (await client.GetAsync("/v1.0/termStore/sets", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray(), IsCostCenters);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/v1.0/extensions/samples.invoices/enable", null, Ct)).StatusCode);
        var set = (await (await client.GetAsync("/v1.0/termStore/sets", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().Single(IsCostCenters);
        var terms = await AllTermsAsync(client, set.GetProperty("id").GetGuid());
        Assert.Equal(["Facilities", "IT", "Marketing", "Operations", "Sales"], terms.Select(t => t.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        Assert.Contains("Information technology", terms.Single(t => t.GetProperty("name").GetString() == "IT").GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()));

        // Enabling again is idempotent.
        await client.PostAsync("/v1.0/extensions/samples.invoices/disable", null, Ct);
        await client.PostAsync("/v1.0/extensions/samples.invoices/enable", null, Ct);
        Assert.Single((await (await client.GetAsync("/v1.0/termStore/sets", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray(), IsCostCenters);
    }
}
