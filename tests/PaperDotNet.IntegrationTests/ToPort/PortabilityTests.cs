using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Provisioning.Data;
using PaperDotNet.Provisioning.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Export and import with the admin CLI (PLT-13): ported with the CLI (T16).</summary>
public sealed class PortabilityTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> OperationAsync(HttpClient client, HttpResponseMessage accepted)
    {
        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(Ct));
        var location = accepted.Headers.Location!.ToString();
        var operation = await Eventually.WaitForAsync(async () =>
        {
            var body = await (await client.GetAsync(location, Ct)).ReadJsonAsync();
            return body.GetProperty("status").GetString() is "succeeded" or "failed" ? (JsonElement?)body : null;
        }, TimeSpan.FromSeconds(60));
        Assert.True(operation.GetProperty("status").GetString() == "succeeded", operation.ToString());
        return operation.GetProperty("result");
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, byte[] package, string query = "")
    {
        var content = new ByteArrayContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return client.PostAsync($"/v1.0/portability/imports{query}", content, Ct);
    }

    private static async Task<List<string>> WorkspaceNamesAsync(HttpClient client) =>
        [.. (await (await client.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().Select(w => w.GetProperty("name").GetString()!)];

    private static async Task<string> CreateUserAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return name;
    }

    [Fact]
    public async Task The_cli_exports_a_tenant_and_imports_it_elsewhere()
    {
        await factory.CreateTenantAsync("port-cli-src");
        var source = await ApiClient.CreateAsync(factory, "port-cli-src");
        var ws = await source.CreateWorkspaceAsync("Handbook");
        var contentType = await source.CreateContentTypeAsync("Page", [new { name = "text", type = "note" }]);
        var list = await source.CreateListAsync(ws, "Pages", contentType);
        await source.CreateItemAsync(ws, list, new { fields = new { title = "Intro", text = "Hello" } });
        await factory.CreateTenantAsync("port-cli-dst");

        var path = Path.Combine(Path.GetTempPath(), $"paperdotnet-cli-{Guid.NewGuid():N}.zip");
        try
        {
            Assert.Equal(0, await PaperDotNet.Host.Cli.AdminCli.RunAsync(factory.Services, ["export", "--tenant", "port-cli-src", "--user", "admin", "-o", path]));
            Assert.True(new FileInfo(path).Length > 0);
            Assert.Equal(1, await PaperDotNet.Host.Cli.AdminCli.RunAsync(factory.Services, ["import", path, "--tenant", "port-cli-dst", "--user", "nobody"]));
            Assert.Equal(0, await PaperDotNet.Host.Cli.AdminCli.RunAsync(factory.Services, ["import", path, "--tenant", "port-cli-dst", "--user", "admin"]));
        }
        finally
        {
            File.Delete(path);
        }

        var target = await ApiClient.CreateAsync(factory, "port-cli-dst");
        var targetWs = (await (await target.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "Handbook").GetProperty("id").GetGuid();
        var targetList = (await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists", Ct)).ReadJsonAsync()).EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Pages").GetProperty("id").GetGuid();
        Assert.Equal(["Intro"], await target.QueryTitlesAsync(targetWs, targetList, ""));
    }
}
