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

/// <summary>Export and import of workspaces and tenants with their content (PLT-13).</summary>
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

    [Fact]
    public async Task Workspaces_are_exported_downloaded_and_imported_as_operations()
    {
        await factory.CreateTenantAsync("port-src");
        var source = await ApiClient.CreateAsync(factory, "port-src");
        var ws = await source.CreateWorkspaceAsync("Knowledge");
        var notes = (await (await source.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Notes", templateKey = "notes" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        await source.CreateItemAsync(ws, notes, new { fields = new { title = "Welcome", body = "Start here. See [[Setup]] #onboarding" } });
        await source.CreateItemAsync(ws, notes, new { fields = new { title = "Setup", body = "Install it." } });

        // Export: an operation, then the package is ready for download.
        var started = await source.PostAsJsonAsync("/v1.0/portability/exports", new { workspaceId = ws }, Ct);
        var exportId = (await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await OperationAsync(source, started);
        var export = await (await source.GetAsync($"/v1.0/portability/exports/{exportId}", Ct)).ReadJsonAsync();
        Assert.True(export.GetProperty("ready").GetBoolean());
        var exports = await (await source.GetAsync("/v1.0/portability/exports", Ct)).ReadJsonAsync();
        Assert.Contains(exports.EnumerateArray(), e => e.GetProperty("id").GetGuid() == exportId);
        var download = await source.GetAsync(export.GetProperty("packageUrl").GetString(), Ct);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        var package = await download.Content.ReadAsByteArrayAsync(Ct);
        using (var zip = new ZipArchive(new MemoryStream(package)))
        {
            Assert.NotNull(zip.GetEntry("template.xml"));
        }

        // Import: a dry run changes nothing, the import creates the workspace with its notes.
        await factory.CreateTenantAsync("port-dst");
        var target = await ApiClient.CreateAsync(factory, "port-dst");
        var plan = await OperationAsync(target, await ImportAsync(target, package, "?dryRun=true"));
        Assert.True(plan.TryGetProperty("dryRun", out var dry) && dry.GetBoolean(), plan.ToString());
        Assert.DoesNotContain("Knowledge", await WorkspaceNamesAsync(target));
        var imported = await OperationAsync(target, await ImportAsync(target, package));
        Assert.False(imported.GetProperty("dryRun").GetBoolean());
        var targetWs = (await (await target.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "Knowledge").GetProperty("id").GetGuid();
        var targetNotes = (await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists", Ct)).ReadJsonAsync()).EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Equal(["Setup", "Welcome"], (await target.QueryTitlesAsync(targetWs, targetNotes, "")).Order(StringComparer.Ordinal));

        // Imported notes work like any other: the link resolves in the target.
        var welcome = (await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists/{targetNotes}/items", Ct)).ReadJsonAsync())
            .GetProperty("value").EnumerateArray().Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "Welcome").GetProperty("id").GetGuid();
        await Eventually.WaitForAsync(async () =>
        {
            var links = await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists/{targetNotes}/items/{welcome}/noteLinks", Ct)).ReadJsonAsync();
            return links.GetProperty("value").EnumerateArray().Any(l => l.TryGetProperty("note", out _)) ? true : (bool?)null;
        });

        // Invalid packages end with the template's errors.
        var invalid = await OperationAsync(target, await ImportAsync(target, "PK not really"u8.ToArray()));
        Assert.NotEmpty(invalid.GetProperty("errors").EnumerateArray());

        // Exports belong to their creator and tenant.
        Assert.Equal(HttpStatusCode.NotFound, (await target.GetAsync($"/v1.0/portability/exports/{exportId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await target.GetAsync($"/v1.0/portability/exports/{exportId}/package", Ct)).StatusCode);
        var member = await ApiClient.CreateAsync(factory, "port-src", await CreateUserAsync(source, "bob"), "bob-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/v1.0/portability/exports", new { workspaceId = ws }, Ct)).StatusCode);

        // Expired exports are removed with their file.
        PaperDotNet.Tenancy.Contracts.TenantSummary tenant;
        await using (var root = factory.Services.CreateAsyncScope())
        {
            tenant = (await root.ServiceProvider.GetRequiredService<PaperDotNet.Tenancy.Contracts.ITenantDirectory>().FindAsync("port-src", Ct))!;
        }

        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
            await db.Packages.Where(p => p.Id == exportId).ExecuteUpdateAsync(u => u.SetProperty(p => p.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)), Ct);
            await scope.ServiceProvider.GetRequiredService<PortabilityCleanupJob>().RunAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await source.GetAsync($"/v1.0/portability/exports/{exportId}", Ct)).StatusCode);
    }

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
