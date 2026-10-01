using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Provisioning.Data;
using PaperDotNet.Provisioning.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Export and import of workspaces and tenants with their content as operations (PLT-13).</summary>
public sealed class PortabilityTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>Waits for the operation of a 202 response and returns its result (it must succeed).</summary>
    private static async Task<JsonElement> OperationAsync(HttpClient client, HttpResponseMessage accepted)
    {
        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(Ct));
        var location = accepted.Headers.Location!.ToString();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var operation = await (await client.GetAsync(location, Ct)).JsonAsync(HttpStatusCode.OK);
            var status = operation.GetProperty("status").GetString();
            if (status is "succeeded" or "failed")
            {
                Assert.True(status == "succeeded", operation.ToString());
                return operation.GetProperty("result");
            }

            Assert.True(DateTime.UtcNow < deadline, operation.ToString());
            await Task.Delay(100, Ct);
        }
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, byte[] package, string query = "")
    {
        var content = new ByteArrayContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return client.PostAsync($"/v1.0/portability/imports{query}", content, Ct);
    }

    private static async Task<List<JsonElement>> GetListAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        var body = await response.JsonAsync(HttpStatusCode.OK);
        return [.. (body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("value")).EnumerateArray()];
    }

    [Fact]
    public async Task Workspaces_are_exported_downloaded_and_imported_as_operations()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Knowledge");
        using var created = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Notes", templateKey = "notes" }, Ct);
        var notes = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        await Api.CreateItemAsync(_admin, ws, notes, new { title = "Welcome", body = "Start here. See [[Setup]] #onboarding" });
        await Api.CreateItemAsync(_admin, ws, notes, new { title = "Setup", body = "Install it." });

        // Export: an operation, then the package is ready for download.
        using var started = await _admin.PostAsJsonAsync("/v1.0/portability/exports", new { workspaceId = ws }, Ct);
        var exportId = (await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).Id();
        await OperationAsync(_admin, started);
        var export = await (await _admin.GetAsync($"/v1.0/portability/exports/{exportId}", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.True(export.GetProperty("ready").GetBoolean());
        Assert.Contains(await GetListAsync(_admin, "/v1.0/portability/exports"), e => e.Id() == exportId);
        using var download = await _admin.GetAsync(export.GetProperty("packageUrl").GetString(), Ct);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        var package = await download.Content.ReadAsByteArrayAsync(Ct);
        using (var zip = new ZipArchive(new MemoryStream(package)))
        {
            Assert.NotNull(zip.GetEntry("template.xml"));
        }

        // Import: a dry run changes nothing, the import creates the workspace with its notes.
        var target = await _host.CreateTenantAsync("port-dst");
        var plan = await OperationAsync(target, await ImportAsync(target, package, "?dryRun=true"));
        Assert.True(plan.GetProperty("dryRun").GetBoolean(), plan.ToString());
        Assert.Empty(await GetListAsync(target, "/v1.0/workspaces"));
        var imported = await OperationAsync(target, await ImportAsync(target, package));
        Assert.False(imported.GetProperty("dryRun").GetBoolean());
        var targetWs = (await GetListAsync(target, "/v1.0/workspaces")).Single(w => w.GetProperty("name").GetString() == "Knowledge").Id();
        var targetNotes = Assert.Single(await GetListAsync(target, $"/v1.0/workspaces/{targetWs}/lists")).Id();
        var items = await GetListAsync(target, Api.Items(targetWs, targetNotes));
        Assert.Equal(["Setup", "Welcome"], items.Select(i => i.GetProperty("fields").GetProperty("title").GetString()).Order(StringComparer.Ordinal));

        // Imported notes work like any other: the link resolves in the target.
        var welcome = items.Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "Welcome").Id();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await GetListAsync(target, $"{Api.Items(targetWs, targetNotes)}/{welcome}/noteLinks")).Any(l => l.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.Object))
        {
            Assert.True(DateTime.UtcNow < deadline, "The imported link did not resolve.");
            await Task.Delay(100, Ct);
        }

        // Invalid packages end with the template's errors.
        var invalid = await OperationAsync(target, await ImportAsync(target, "PK not really"u8.ToArray()));
        Assert.NotEmpty(invalid.GetProperty("errors").EnumerateArray());

        // Exports belong to their creator and tenant; members cannot export.
        Assert.Equal(HttpStatusCode.NotFound, (await target.GetAsync($"/v1.0/portability/exports/{exportId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await target.GetAsync($"/v1.0/portability/exports/{exportId}/package", Ct)).StatusCode);
        using (var bob = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = "bob", password = "bob-password-1" }, Ct))
        {
            await bob.JsonAsync(HttpStatusCode.Created);
        }

        var member = await _host.SignInAsync("bob", "bob-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/v1.0/portability/exports", new { workspaceId = ws }, Ct)).StatusCode);

        // Expired exports are removed with their file.
        var tenantId = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
            var id = Guid.Parse(exportId);
            var row = await db.Packages.SingleAsync(p => p.TenantId == tenantId && p.Id == id, Ct);
            row.ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
            await db.SaveChangesAsync(Ct);
            await ActivatorUtilities.CreateInstance<PortabilityCleanupJob>(scope.ServiceProvider).RunAsync(tenantId, Ct);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/v1.0/portability/exports/{exportId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/v1.0/portability/exports/{exportId}/package", Ct)).StatusCode);
    }
}
