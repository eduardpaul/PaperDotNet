using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.IntegrationTests;

public sealed class SearchWorkflowTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Client, Guid Workspace, Guid List, string Url, TenantSummary Tenant)> SetupAsync(string tenant, bool library = false)
    {
        var createdTenant = await factory.CreateTenantAsync(tenant);
        var client = await ApiClient.CreateAsync(factory, tenant);
        var ws = await client.CreateWorkspaceAsync("Office");
        Guid list;
        if (library)
        {
            var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Files", templateKey = "documents" }, Ct);
            list = (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        }
        else
        {
            var type = await client.CreateContentTypeAsync("Record", [new { name = "text", type = "note" }]);
            list = await client.CreateListAsync(ws, "Records", type);
        }
        return (client, ws, list, $"/v1.0/workspaces/{ws}/lists/{list}", createdTenant);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.ReadJsonAsync();
    }

    private static async Task<JsonElement> IndexedAsync(HttpClient client, string itemUrl)
    {
        JsonElement status = default;
        await Eventually.WaitForAsync<bool>(async () =>
        {
            status = await GetAsync(client, $"{itemUrl}/searchIndex");
            return status.GetProperty("state").GetString() == "indexed" ? true : null;
        }, TimeSpan.FromSeconds(60));
        return status;
    }

    [Fact]
    public async Task Ordinary_lists_have_visible_indexing_workflows_and_manual_indexing_when_automatic_is_off()
    {
        var s = await SetupAsync("wf-search-manual");
        var builtIns = await GetAsync(s.Client, $"{s.Url}/workflows/builtIns");
        var indexing = builtIns.EnumerateArray().Single(w => w.GetProperty("key").GetString() == "search.index");
        Assert.Equal("list", indexing.GetProperty("scope").GetString());
        Assert.True(indexing.GetProperty("enabled").GetBoolean(), indexing.ToString());
        var off = await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Url}/workflows/builtIns/search.index",
            indexing.GetProperty("@odata.etag").GetString()!, new { enabled = false });
        Assert.True(off.IsSuccessStatusCode, await off.Content.ReadAsStringAsync(Ct));
        var id = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Manual index", text = "A physician reads this record" } })).GetProperty("id").GetGuid();
        var itemUrl = $"{s.Url}/items/{id}";
        Assert.Equal("notIndexed", (await GetAsync(s.Client, $"{itemUrl}/searchIndex")).GetProperty("state").GetString());
        var started = await s.Client.PostAsJsonAsync($"{s.Url}/workflows/builtIns/search.index/runs", new { listId = s.List, itemIds = new[] { id } }, Ct);
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync(Ct));
        var status = await IndexedAsync(s.Client, itemUrl);
        Assert.Equal(status.GetProperty("currentRevision").GetString(), status.GetProperty("indexedRevision").GetString());
        var runId = status.GetProperty("run").GetProperty("id").GetGuid();
        var run = await GetAsync(s.Client, $"/v1.0/workspaces/{s.Workspace}/workflows/runs/{runId}");
        Assert.True(run.GetProperty("outputs").TryGetProperty("chunk", out _));
        Assert.True(run.GetProperty("outputs").TryGetProperty("publish", out _));
        Assert.True(run.GetProperty("outputs").TryGetProperty("embed", out _));
        Assert.DoesNotContain("physician", run.GetProperty("outputs").ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False((await GetAsync(s.Client, $"{s.Url}/workflows/builtIns")).EnumerateArray()
            .Single(w => w.GetProperty("key").GetString() == "search.index").GetProperty("enabled").GetBoolean());

        // A completed historical run cannot make a changed source appear current.
        var etag = (await s.Client.GetAsync(itemUrl, Ct)).Headers.ETag!.Tag;
        await s.Client.SendWithEtagAsync(HttpMethod.Patch, itemUrl, etag, new { fields = new { title = "Changed after indexing" } });
        Assert.Equal("stale", (await GetAsync(s.Client, $"{itemUrl}/searchIndex")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Library_exclusion_hides_existing_records_and_blocks_manual_updates_and_rebuilds_until_reenabled()
    {
        var s = await SetupAsync("wf-search-exclude", library: true);
        var id = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Confidential orchid" } })).GetProperty("id").GetGuid();
        var itemUrl = $"{s.Url}/items/{id}";
        await IndexedAsync(s.Client, itemUrl);
        var settings = await GetAsync(s.Client, $"{s.Url}/searchSettings");
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await s.Client.PutAsJsonAsync($"{s.Url}/searchSettings", new { included = false }, Ct)).StatusCode);
        var excluded = await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Url}/searchSettings", settings.GetProperty("@odata.etag").GetString()!, new { included = false });
        Assert.True(excluded.IsSuccessStatusCode, await excluded.Content.ReadAsStringAsync(Ct));
        foreach (var query in new[] { "q=orchid&mode=keyword", "q=orchid&mode=semantic", "q=orchid&mode=hybrid", $"containerId={s.List}" })
        {
            var found = await GetAsync(s.Client, $"/v1.0/search?{query}");
            Assert.Empty(found.GetProperty("value").EnumerateArray());
            Assert.Equal(0, found.GetProperty("@odata.count").GetInt32());
            Assert.Empty(found.GetProperty("facets").GetProperty("container").EnumerateArray());
        }
        Assert.Equal("excluded", (await GetAsync(s.Client, $"{itemUrl}/searchIndex")).GetProperty("state").GetString());
        var manual = await s.Client.PostAsJsonAsync($"{s.Url}/workflows/builtIns/search.index/runs", new { listId = s.List, itemIds = new[] { id } }, Ct);
        Assert.True(manual.IsSuccessStatusCode);
        var runId = (await manual.ReadJsonAsync())[0].GetProperty("id").GetGuid();
        await Eventually.WaitForAsync<bool>(async () => (await GetAsync(s.Client, $"/v1.0/workspaces/{s.Workspace}/workflows/runs/{runId}")).GetProperty("status").GetString() == "failed" ? true : null);
        var operation = await s.Client.PostAsync("/v1.0/search/reindex", null, Ct);
        await Eventually.WaitForAsync<bool>(async () => (await GetAsync(s.Client, operation.Headers.Location!.ToString())).GetProperty("status").GetString() == "succeeded" ? true : null, TimeSpan.FromSeconds(60));
        Assert.Empty((await GetAsync(s.Client, "/v1.0/search?q=orchid")).GetProperty("value").EnumerateArray());

        settings = await GetAsync(s.Client, $"{s.Url}/searchSettings");
        var included = await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Url}/searchSettings", settings.GetProperty("@odata.etag").GetString()!, new { included = true });
        Assert.True(included.IsSuccessStatusCode);
        await IndexedAsync(s.Client, itemUrl);
        Assert.Single((await GetAsync(s.Client, "/v1.0/search?q=orchid")).GetProperty("value").EnumerateArray());

        await factory.CreateTenantAsync("wf-search-foreign");
        var foreign = await ApiClient.CreateAsync(factory, "wf-search-foreign");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{itemUrl}/searchIndex", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{s.Url}/searchSettings", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.SendWithEtagAsync(HttpMethod.Put, $"{s.Url}/searchSettings", settings.GetProperty("@odata.etag").GetString()!, new { included = false })).StatusCode);
    }

    [Fact]
    public async Task Embedding_failure_is_visible_keyword_publication_remains_available_and_retry_reuses_published_chunks()
    {
        var s = await SetupAsync("wf-search-failure");
        var marker = $"providerfailure{Guid.NewGuid():N}";
        ConceptEmbeddingGenerator.Instance.FailingInputs[marker] = true;
        try
        {
            var id = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Retryable embedding", text = marker } })).GetProperty("id").GetGuid();
            var itemUrl = $"{s.Url}/items/{id}";
            JsonElement status = default;
            await Eventually.WaitForAsync<bool>(async () =>
            {
                status = await GetAsync(s.Client, $"{itemUrl}/searchIndex");
                return status.GetProperty("state").GetString() == "failed" ? true : null;
            }, TimeSpan.FromSeconds(60));
            Assert.True(status.GetProperty("indexed").GetBoolean());
            Assert.Equal("pending", status.GetProperty("embeddingState").GetString());
            Assert.Single((await GetAsync(s.Client, $"/v1.0/search?q={marker}&mode=keyword")).GetProperty("value").EnumerateArray());
            var runId = status.GetProperty("run").GetProperty("id").GetGuid();
            var runUrl = $"/v1.0/workspaces/{s.Workspace}/workflows/runs/{runId}";
            Assert.Equal("embed", (await GetAsync(s.Client, runUrl)).GetProperty("failedNode").GetString());
            ConceptEmbeddingGenerator.Instance.FailingInputs.TryRemove(marker, out _);
            var retry = await s.Client.PostAsync($"{runUrl}/retry", null, Ct);
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync(Ct));
            status = await IndexedAsync(s.Client, itemUrl);
            Assert.Equal("ready", status.GetProperty("embeddingState").GetString());
            var count = ConceptEmbeddingGenerator.Instance.Embedded(marker);
            var etag = (await s.Client.GetAsync(itemUrl, Ct)).Headers.ETag!.Tag;
            var renamed = await s.Client.SendWithEtagAsync(HttpMethod.Patch, itemUrl, etag, new { fields = new { title = "Renamed without reembedding" } });
            Assert.True(renamed.IsSuccessStatusCode);
            await IndexedAsync(s.Client, itemUrl);
            Assert.Equal(count, ConceptEmbeddingGenerator.Instance.Embedded(marker));
        }
        finally { ConceptEmbeddingGenerator.Instance.FailingInputs.TryRemove(marker, out _); }
    }

    [Fact]
    public async Task Staged_generations_do_not_publish_after_source_or_inclusion_changes()
    {
        var s = await SetupAsync("wf-search-staging");
        var builtin = (await GetAsync(s.Client, $"{s.Url}/workflows/builtIns")).EnumerateArray()
            .Single(w => w.GetProperty("key").GetString() == "search.index");
        Assert.True((await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Url}/workflows/builtIns/search.index",
            builtin.GetProperty("@odata.etag").GetString()!, new { enabled = false })).IsSuccessStatusCode);
        var id = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Staged only", text = "Original content" } })).GetProperty("id").GetGuid();
        var tenant = s.Tenant;
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var activities = scope.ServiceProvider.GetServices<IWorkflowActivity>().ToDictionary(a => a.Key);
        var store = scope.ServiceProvider.GetRequiredService<ISearchStore>();
        WorkflowActivityContext Context(Guid run) => new()
        {
            WorkspaceId = s.Workspace,
            Item = new WorkflowItem(s.Workspace, s.List, id),
            RunId = run,
            Inputs = new JsonObject(),
            Services = scope.ServiceProvider,
            Source = "test",
            ExecutionKey = run.ToString(),
            ExecutionId = run,
            ExpandAsync = (value, _) => Task.FromResult(value),
            ResolveAsync = (value, _) => Task.FromResult<JsonNode?>(JsonValue.Create(value)),
        };
        var run = Guid.CreateVersion7();
        Assert.True((await activities["search.chunk"].ExecuteAsync(Context(run), Ct)).Succeeded);
        Assert.NotNull(await store.GetStageAsync(run, Ct));
        Assert.False((await GetAsync(s.Client, $"{s.Url}/items/{id}/searchIndex")).GetProperty("indexed").GetBoolean());
        var itemUrl = $"{s.Url}/items/{id}";
        var etag = (await s.Client.GetAsync(itemUrl, Ct)).Headers.ETag!.Tag;
        Assert.True((await s.Client.SendWithEtagAsync(HttpMethod.Patch, itemUrl, etag, new { fields = new { text = "New content" } })).IsSuccessStatusCode);
        Assert.False((await activities["search.publish"].ExecuteAsync(Context(run), Ct)).Succeeded);
        Assert.Null(await store.GetStageAsync(run, Ct));
        run = Guid.CreateVersion7();
        Assert.True((await activities["search.chunk"].ExecuteAsync(Context(run), Ct)).Succeeded);
        var settings = await GetAsync(s.Client, $"{s.Url}/searchSettings");
        Assert.True((await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Url}/searchSettings",
            settings.GetProperty("@odata.etag").GetString()!, new { included = false })).IsSuccessStatusCode);
        Assert.False((await activities["search.publish"].ExecuteAsync(Context(run), Ct)).Succeeded);
        Assert.Empty((await GetAsync(s.Client, "/v1.0/search?q=content")).GetProperty("value").EnumerateArray());
    }

    [Fact]
    public async Task Item_changes_run_the_other_product_processes_as_workflows()
    {
        var s = await SetupAsync("wf-item-processes");
        var id = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Visible processing" } })).GetProperty("id").GetGuid();
        await IndexedAsync(s.Client, $"{s.Url}/items/{id}");
        await Eventually.WaitForAsync<bool>(async () =>
        {
            var runs = (await GetAsync(s.Client, $"/v1.0/workspaces/{s.Workspace}/workflows/runs?itemId={id}")).GetProperty("value").EnumerateArray().ToList();
            return new[] { "Record item activity", "Update note links", "Notify followers", "Queue change notifications" }
                .All(name => runs.Any(r => r.GetProperty("workflow").GetString() == name && r.GetProperty("status").GetString() == "completed")) ? true : null;
        }, TimeSpan.FromSeconds(60));
    }
}
