using System.Net;
using System.Text.Json.Nodes;

namespace PaperDotNet.IntegrationTests;

public sealed class OpenApiTests : IAsyncLifetime
{
    private readonly TestHost _host = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task The_document_describes_the_OData_query_options()
    {
        var document = await (await _host.CreateClient().GetAsync("/openapi/v1.json")).JsonAsync(HttpStatusCode.OK);
        var parameters = document.GetProperty("paths").GetProperty("/v1.0/workspaces/{workspaceId}/lists/{listId}/items").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(["workspaceId", "listId", "$filter", "$orderby", "$top", "$skiptoken", "$count", "$select", "viewId"], parameters);
    }

    [Fact]
    public async Task The_committed_openapi_document_is_current()
    {
        var current = JsonNode.Parse(await _host.CreateClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))!.AsObject();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PaperDotNet.slnx")))
        {
            root = root.Parent;
        }

        var document = Path.Combine(root!.FullName, "src", "PaperDotNet.Host", "openapi.json");
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(document, TestContext.Current.CancellationToken))!.AsObject();
        current.Remove("servers");
        committed.Remove("servers");
        // The test extension (tests.tickets) is not part of the server's own document.
        foreach (var path in current["paths"]!.AsObject().Select(p => p.Key).Where(p => p.StartsWith("/v1.0/ext/", StringComparison.Ordinal)).ToList())
        {
            current["paths"]!.AsObject().Remove(path);
        }

        current["components"]?["schemas"]?.AsObject().Remove("TicketStats");
        if (current["tags"] is JsonArray tags)
        {
            foreach (var tag in tags.Where(t => t?["name"]?.GetValue<string>().StartsWith("Extension:", StringComparison.Ordinal) == true).ToList())
            {
                tags.Remove(tag);
            }
        }

        var changed = current["paths"]!.AsObject().Select(p => p.Key)
            .Where(p => committed["paths"]![p] is not { } c || !JsonNode.DeepEquals(current["paths"]![p], c)).ToList();
        Assert.True(JsonNode.DeepEquals(current, committed), $"src/PaperDotNet.Host/openapi.json is outdated: run eng/openapi.sh (changed: {string.Join(", ", changed)}).");
    }

    [Fact]
    public async Task Health_endpoints_report_healthy()
    {
        var client = _host.CreateClient();
        foreach (var path in new[] { "/health", "/health/live", "/health/ready" })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
        }
    }
}
