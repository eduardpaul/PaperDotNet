using System.Net;

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
}
