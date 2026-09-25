using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>What generated SDKs rely on (ADR-0032): ETags in bodies, one problem shape, query options, CORS.</summary>
public sealed class SdkContractTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bodies_carry_the_etag_of_the_header_and_lists_carry_one_per_entry()
    {
        await factory.CreateTenantAsync("sdk-etags");
        var client = await ApiClient.CreateAsync(factory, "sdk-etags");
        var ws = await client.CreateWorkspaceAsync("ETags");
        var workspace = await client.GetAsync($"/v1.0/workspaces/{ws}", Ct);
        Assert.Equal(workspace.Headers.ETag!.Tag, (await workspace.ReadJsonAsync()).GetProperty("@odata.etag").GetString());

        var list = await client.CreateListAsync(ws, "Notes");
        var created = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/items", new { fields = new { title = "One" } }, Ct);
        var etag = (await created.ReadJsonAsync()).GetProperty("@odata.etag").GetString();
        Assert.Equal(created.Headers.ETag!.Tag, etag);
        var page = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items", Ct)).ReadJsonAsync();
        Assert.Equal(etag, page.GetProperty("value")[0].GetProperty("@odata.etag").GetString());

        var group = await (await client.PostAsJsonAsync("/v1.0/groups", new { name = "G" }, Ct)).ReadJsonAsync();
        Assert.StartsWith("\"", group.GetProperty("@odata.etag").GetString(), StringComparison.Ordinal);
        var groups = await (await client.GetAsync("/v1.0/groups", Ct)).ReadJsonAsync();
        Assert.All(groups.GetProperty("value").EnumerateArray(), g => Assert.True(g.TryGetProperty("@odata.etag", out _)));
    }

    [Fact]
    public async Task Errors_are_problems_with_a_code()
    {
        await factory.CreateTenantAsync("sdk-problems");
        var client = await ApiClient.CreateAsync(factory, "sdk-problems");
        var missing = await client.GetAsync($"/v1.0/workspaces/{Guid.NewGuid()}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.True((await missing.ReadJsonAsync()).TryGetProperty("code", out _));
    }

    [Fact]
    public async Task The_openapi_document_describes_what_sdks_need()
    {
        var document = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/openapi/v1.json", Ct)).RootElement;
        var paths = document.GetProperty("paths");
        var items = paths.GetProperty("/v1.0/workspaces/{workspaceId}/lists/{listId}/items").GetProperty("get");
        var parameters = items.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("$filter", parameters);
        Assert.Contains("$orderby", parameters);
        Assert.Contains("$skiptoken", parameters);
        Assert.True(items.GetProperty("responses").TryGetProperty("4XX", out _));
        var upload = paths.GetProperty("/v1.0/workspaces/{workspaceId}/lists/{listId}/documents").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
        Assert.Equal("binary", upload.GetProperty("properties").GetProperty("file").GetProperty("format").GetString());
        Assert.True(paths.GetProperty("/v1.0/me/events").GetProperty("get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").TryGetProperty("text/event-stream", out _));
        Assert.Equal("object", document.GetProperty("components").GetProperty("schemas").GetProperty("JsonObject").GetProperty("type").GetString());
    }
}
