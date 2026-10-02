using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

public sealed class TypedRelationshipTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private async Task<(HttpClient Client, Guid First, Guid Second, Guid Third)> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var client = await ApiClient.CreateAsync(factory, tenant);
        var ws = await client.CreateWorkspaceAsync("Graph");
        var type = await client.CreateContentTypeAsync("Node", []);
        var list = await client.CreateListAsync(ws, "Nodes", type);
        async Task<Guid> ItemAsync(string title) => (await client.CreateItemAsync(ws, list, new { fields = new { title } })).GetProperty("id").GetGuid();
        return (client, await ItemAsync("Receipt"), await ItemAsync("Line"), await ItemAsync("Other receipt"));
    }
    private static async Task<List<JsonElement>> EdgesAsync(HttpClient client, Guid id, string query = "") =>
        (await (await client.GetAsync($"/v1.0/items/{id}/relationships{query}", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();

    [Fact]
    public async Task Typed_edges_keep_distinct_identities_and_legacy_links_work_unchanged()
    {
        var (client, first, second, _) = await SetupAsync("typed-distinct");
        (await client.PutAsync($"/v1.0/items/{first}/relations/{second}", null, Ct)).EnsureSuccessStatusCode();
        foreach (var predicate in new[] { "references", "supports" })
        {
            (await client.PostAsJsonAsync($"/v1.0/items/{first}/relationships", new { otherId = second, type = predicate }, Ct)).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync($"/v1.0/items/{second}/relationships", new { otherId = first, type = predicate }, Ct)).EnsureSuccessStatusCode();
        }
        var edges = await EdgesAsync(client, first);
        Assert.Equal(3, edges.Count);
        Assert.Equal(3, edges.Select(e => e.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.Equal(3, (await EdgesAsync(client, second)).Count);
        (await client.DeleteAsync($"/v1.0/items/{second}/relationships/{edges.Single(e => !e.TryGetProperty("type", out _)).GetProperty("id").GetGuid()}", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(2, (await EdgesAsync(client, first)).Count);
        Assert.Single((await (await client.GetAsync($"/v1.0/items/{first}/relations", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray());
        var page = await (await client.GetAsync($"/v1.0/items/{first}/relationships?$top=1", Ct)).ReadJsonAsync();
        Assert.Single(page.GetProperty("value").EnumerateArray());
        Assert.Single((await (await client.GetAsync(page.GetProperty("@odata.nextLink").GetString(), Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray());
    }

    [Fact]
    public async Task Direction_inverse_labels_limits_and_type_validation_are_enforced()
    {
        var (client, first, second, third) = await SetupAsync("typed-direction");
        var response = await client.PostAsJsonAsync("/v1.0/relationshipTypes", new { name = "contains", directed = true, inverseLabel = "belongs to", maxIncoming = 1 }, Ct);
        response.EnsureSuccessStatusCode();
        var type = (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync($"/v1.0/items/{first}/relationships", new { otherId = second, type }, Ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/v1.0/items/{first}/relationships", new { otherId = second, type }, Ct)).EnsureSuccessStatusCode();
        var edge = Assert.Single(await EdgesAsync(client, second, "?direction=incoming&type=contains"));
        Assert.Equal(first, edge.GetProperty("sourceItemId").GetGuid());
        Assert.Equal("belongs to", edge.GetProperty("type").GetProperty("inverseLabel").GetString());
        Assert.Empty(await EdgesAsync(client, second, "?direction=outgoing"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/v1.0/items/{third}/relationships", new { otherId = second, type }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/v1.0/items/{first}/relationships", new { otherId = third, type, directed = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/v1.0/relationshipTypes", new { name = "contains", directed = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/v1.0/items/{first}/relationships?direction=sideways", Ct)).StatusCode);
        (await client.DeleteAsync($"/v1.0/items/{second}/relationships/{edge.GetProperty("id").GetGuid()}", Ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/v1.0/items/{third}/relationships", new { otherId = second, type }, Ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Concurrent_writers_cannot_exceed_endpoint_cardinality()
    {
        var (client, first, second, third) = await SetupAsync("typed-concurrent");
        (await client.PostAsJsonAsync("/v1.0/relationshipTypes", new { name = "owns", directed = true, maxIncoming = 1 }, Ct)).EnsureSuccessStatusCode();
        var responses = await Task.WhenAll(new[] { first, third }.Select(id => client.PostAsJsonAsync($"/v1.0/items/{id}/relationships", new { otherId = second, type = "owns" }, Ct)));
        Assert.Single(responses, r => r.IsSuccessStatusCode);
        Assert.Single(await EdgesAsync(client, second));
    }

    [Fact]
    public async Task Graph_endpoints_and_types_are_tenant_isolated()
    {
        var (owner, first, second, _) = await SetupAsync("typed-owner");
        (await owner.PostAsJsonAsync($"/v1.0/items/{first}/relationships", new { otherId = second, type = "tenant secret" }, Ct)).EnsureSuccessStatusCode();
        var edge = Assert.Single(await EdgesAsync(owner, first));
        var (foreign, foreignItem, _, _) = await SetupAsync("typed-foreign");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/v1.0/items/{first}/relationships", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"/v1.0/items/{foreignItem}/relationships", new { otherId = first }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.DeleteAsync($"/v1.0/items/{foreignItem}/relationships/{edge.GetProperty("id").GetGuid()}", Ct)).StatusCode);
        Assert.Empty((await (await foreign.GetAsync("/v1.0/relationshipTypes", Ct)).ReadJsonAsync()).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await foreign.PostAsJsonAsync($"/v1.0/items/{foreignItem}/relationships", new { otherId = foreignItem }, Ct)).StatusCode);
    }
    [Fact]
    public async Task Taxonomy_merges_preserve_graph_links_and_deduplicate_matching_edges()
    {
        var (client, first, second, _) = await SetupAsync("typed-merge");
        foreach (var name in new[] { "references", "cites" })
            (await client.PostAsJsonAsync($"/v1.0/items/{first}/relationships", new { otherId = second, type = name, attributes = new Dictionary<string, object> { [name] = true } }, Ct)).EnsureSuccessStatusCode();
        var types = (await (await client.GetAsync("/v1.0/relationshipTypes", Ct)).ReadJsonAsync()).EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("id").GetGuid());
        var term = Assert.Single((await (await client.GetAsync($"/v1.0/termStore/terms?ids={types["references"]}", Ct)).ReadJsonAsync()).EnumerateArray());
        var set = term.GetProperty("termSetId").GetGuid();
        (await client.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms/{types["references"]}/merge", new { targetTermId = types["cites"] }, Ct)).EnsureSuccessStatusCode();
        await Eventually.WaitForAsync<bool>(async () => (await EdgesAsync(client, first)).Count == 1 ? true : null);
        Assert.Equal("cites", Assert.Single(await EdgesAsync(client, second)).GetProperty("type").GetProperty("name").GetString());
        (await client.PostAsJsonAsync($"/v1.0/items/{first}/relationships", new { otherId = second, type = "references" }, Ct)).EnsureSuccessStatusCode();
        var mergedAttributes = Assert.Single(await EdgesAsync(client, first)).GetProperty("attributes");
        Assert.True(mergedAttributes.GetProperty("references").GetBoolean());
        Assert.True(mergedAttributes.GetProperty("cites").GetBoolean());
        Assert.Single((await (await client.GetAsync("/v1.0/relationshipTypes", Ct)).ReadJsonAsync()).EnumerateArray());
    }

    [Fact]
    public async Task Taxonomy_cannot_merge_constrained_predicates_with_existing_edges()
    {
        var (client, first, second, _) = await SetupAsync("typed-merge-guard");
        var ids = new List<Guid>();
        foreach (var name in new[] { "owns", "contains" })
        {
            var response = await client.PostAsJsonAsync("/v1.0/relationshipTypes", new { name, directed = true, maxIncoming = 1 }, Ct);
            response.EnsureSuccessStatusCode();
            ids.Add((await response.ReadJsonAsync()).GetProperty("id").GetGuid());
        }
        (await client.PostAsJsonAsync($"/v1.0/items/{first}/relationships", new { otherId = second, type = ids[0] }, Ct)).EnsureSuccessStatusCode();
        var term = Assert.Single((await (await client.GetAsync($"/v1.0/termStore/terms?ids={ids[0]}", Ct)).ReadJsonAsync()).EnumerateArray());
        var responseMerge = await client.PostAsJsonAsync($"/v1.0/termStore/sets/{term.GetProperty("termSetId").GetGuid()}/terms/{ids[0]}/merge", new { targetTermId = ids[1] }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, responseMerge.StatusCode);
        Assert.Equal("owns", Assert.Single(await EdgesAsync(client, first)).GetProperty("type").GetProperty("name").GetString());
    }

}
