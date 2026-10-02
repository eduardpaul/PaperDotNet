using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

public sealed class RelationshipAttributeTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed record World(HttpClient Client, Guid Workspace, Guid OtherWorkspace, Guid List, Guid OtherList, Guid First, Guid Second);
    private async Task<World> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var client = await ApiClient.CreateAsync(factory, tenant);
        var ws = await client.CreateWorkspaceAsync("Graph");
        var other = await client.CreateWorkspaceAsync("Other graph");
        var type = await client.CreateContentTypeAsync("Node", []);
        var list = await client.CreateListAsync(ws, "Nodes", type);
        var otherList = await client.CreateListAsync(other, "Other nodes", type);
        var first = (await client.CreateItemAsync(ws, list, new { fields = new { title = "First" } })).GetProperty("id").GetGuid();
        var second = (await client.CreateItemAsync(other, otherList, new { fields = new { title = "Second" } })).GetProperty("id").GetGuid();
        return new(client, ws, other, list, otherList, first, second);
    }
    private static async Task<JsonElement> PageAsync(World world, string filter, Guid? workspace = null, int top = 50)
    {
        var response = await world.Client.GetAsync($"/v1.0/workspaces/{workspace ?? world.Workspace}/relationships?$top={top}&$filter={Uri.EscapeDataString(filter)}", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadJsonAsync();
    }
    private static async Task<Guid> AddAsync(World world, string type, object attributes)
    {
        (await world.Client.PostAsJsonAsync($"/v1.0/items/{world.First}/relationships", new { otherId = world.Second, type, directed = true, attributes }, Ct)).EnsureSuccessStatusCode();
        return Assert.Single((await (await world.Client.GetAsync($"/v1.0/items/{world.First}/relationships?type={type}", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Workspace_filters_return_item_pairs_and_handle_missing_and_mixed_types_before_paging()
    {
        var world = await SetupAsync("attribute-query");
        await AddAsync(world, "low", new { confidence = 0.6, origin = "reader", approved = false });
        await AddAsync(world, "high", new { confidence = 0.9, approved = true });
        await AddAsync(world, "text", new { confidence = "unknown" });
        await AddAsync(world, "missing", new { origin = "manual" });
        var page = await PageAsync(world, "attributes/confidence le 0.7");
        var edge = Assert.Single(page.GetProperty("value").EnumerateArray());
        Assert.Equal(world.First, edge.GetProperty("sourceItem").GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal(world.Second, edge.GetProperty("targetItem").GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal(world.OtherWorkspace, edge.GetProperty("targetItem").GetProperty("workspaceId").GetGuid());
        Assert.Equal(0.6, edge.GetProperty("attributes").GetProperty("confidence").GetDouble());
        Assert.Single((await PageAsync(world, "0.7 ge attributes/confidence and attributes/approved eq false")).GetProperty("value").EnumerateArray());
        Assert.Single((await PageAsync(world, "attributes/confidence eq 'unknown'")).GetProperty("value").EnumerateArray());
        Assert.Single((await PageAsync(world, "attributes/confidence eq null")).GetProperty("value").EnumerateArray());
        Assert.Equal(2, (await PageAsync(world, "attributes/confidence ne 0.7")).GetProperty("value").GetArrayLength());
        var first = await PageAsync(world, "attributes/confidence le 0.7 or attributes/approved eq true", top: 1);
        Assert.Single(first.GetProperty("value").EnumerateArray());
        var nextUrl = first.GetProperty("@odata.nextLink").GetString();
        Assert.Contains("%24filter=", nextUrl);
        Assert.Single((await (await world.Client.GetAsync(nextUrl, Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray());
        Assert.Equal(4, (await PageAsync(world, "directed eq true", world.OtherWorkspace)).GetProperty("value").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await world.Client.GetAsync($"/v1.0/workspaces/{world.Workspace}/relationships?$filter=attributes/confidence/other%20eq%201", Ct)).StatusCode);
        var etag = (await world.Client.GetAsync($"/v1.0/items/{world.First}", Ct)).Headers.ETag!.Tag;
        (await world.Client.SendWithEtagAsync(HttpMethod.Post, $"/v1.0/items/{world.First}/move", etag, new { workspaceId = world.OtherWorkspace, listId = world.OtherList })).EnsureSuccessStatusCode();
        Assert.Empty((await PageAsync(world, "true")).GetProperty("value").EnumerateArray());
        Assert.Equal(4, (await PageAsync(world, "true", world.OtherWorkspace)).GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Attributes_are_idempotent_on_create_and_patch_uses_edge_versions_and_scalar_validation()
    {
        var world = await SetupAsync("attribute-patch");
        var id = await AddAsync(world, "references", new { confidence = 0.6, origin = "reader" });
        await AddAsync(world, "references", new { confidence = 0.9 });
        var url = $"/v1.0/items/{world.Second}/relationships/{id}";
        var read = await world.Client.GetAsync(url, Ct);
        var initial = await read.ReadJsonAsync();
        Assert.Equal(0.6, initial.GetProperty("attributes").GetProperty("confidence").GetDouble());
        var etag = read.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await world.Client.PatchAsJsonAsync(url, new { attributes = new { confidence = 0.7 } }, Ct)).StatusCode);
        var patched = await world.Client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { attributes = new Dictionary<string, object?> { ["confidence"] = 0.7, ["origin"] = null, ["reader_confidence"] = 0.5 } });
        Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);
        Assert.NotEqual(etag, patched.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await world.Client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { attributes = new { confidence = 1 } })).StatusCode);
        var current = await (await world.Client.GetAsync(url, Ct)).ReadJsonAsync();
        Assert.False(current.GetProperty("attributes").TryGetProperty("origin", out _));
        Assert.Single((await PageAsync(world, "attributes/reader_confidence le 0.7")).GetProperty("value").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await world.Client.SendWithEtagAsync(HttpMethod.Patch, url, patched.Headers.ETag!.Tag, new { attributes = new { nested = new { value = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await world.Client.PostAsJsonAsync($"/v1.0/items/{world.First}/relationships", new { otherId = world.Second, attributes = new Dictionary<string, object> { ["bad\"key"] = 1 } }, Ct)).StatusCode);
        var concurrent = await Task.WhenAll(new[] { 0.8, 0.9 }.Select(confidence => world.Client.SendWithEtagAsync(HttpMethod.Patch, url, patched.Headers.ETag!.Tag, new { attributes = new { confidence } })));
        Assert.Single(concurrent, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(concurrent, r => r.StatusCode == HttpStatusCode.PreconditionFailed);

    }

    [Fact]
    public async Task Workspace_queries_and_edge_updates_are_tenant_isolated()
    {
        var world = await SetupAsync("attribute-owner");
        var edge = await AddAsync(world, "private", new { confidence = 0.1 });
        var foreign = await SetupAsync("attribute-foreign");
        var url = $"/v1.0/items/{world.First}/relationships/{edge}";
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.Client.GetAsync($"/v1.0/workspaces/{world.Workspace}/relationships", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.Client.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.Client.SendWithEtagAsync(HttpMethod.Patch, url, "\"1\"", new { attributes = new { confidence = 1 } })).StatusCode);
        Assert.Empty((await PageAsync(foreign, "true")).GetProperty("value").EnumerateArray());
    }
    [Fact]
    public async Task Taxonomy_merges_reject_attribute_conflicts_without_losing_edges()
    {
        var world = await SetupAsync("attribute-merge-conflict");
        var firstId = await AddAsync(world, "references", new { confidence = 0.6 });
        var secondId = await AddAsync(world, "cites", new { confidence = 0.9 });
        var page = await PageAsync(world, "true");
        var edges = page.GetProperty("value").EnumerateArray().ToList();
        var sourceType = edges.Single(e => e.GetProperty("id").GetGuid() == firstId).GetProperty("type").GetProperty("id").GetGuid();
        var targetType = edges.Single(e => e.GetProperty("id").GetGuid() == secondId).GetProperty("type").GetProperty("id").GetGuid();
        var term = Assert.Single((await (await world.Client.GetAsync($"/v1.0/termStore/terms?ids={sourceType}", Ct)).ReadJsonAsync()).EnumerateArray());
        var set = term.GetProperty("termSetId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await world.Client.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms/{sourceType}/merge", new { targetTermId = targetType }, Ct)).StatusCode);
        Assert.Equal(2, (await PageAsync(world, "true")).GetProperty("value").GetArrayLength());
    }

}
