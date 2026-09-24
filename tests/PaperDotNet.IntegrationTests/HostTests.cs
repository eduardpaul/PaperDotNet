using System.Net;

namespace PaperDotNet.IntegrationTests;

public sealed class HostTests(PaperDotNetApiFactory factory)
{
    [Fact]
    public async Task Health_endpoints_report_healthy()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_v1_api()
    {
        var client = factory.CreateClient();

        var document = await (await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).ReadJsonAsync();

        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/v1.0/auth/token", paths);
        Assert.Contains("/v1.0/me", paths);
        Assert.Contains("/v1.0/workspaces/{id}", paths);
        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearer", out _));
    }
}
