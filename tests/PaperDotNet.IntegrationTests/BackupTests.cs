using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Host.Backup;

namespace PaperDotNet.IntegrationTests;

/// <summary>Backup and restore (PLT-12): an installation moves into a fresh one with its data and files.</summary>
public sealed class BackupTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Backup_and_restore_move_an_installation()
    {
        await factory.CreateTenantAsync("backup-src");
        var source = await ApiClient.CreateAsync(factory, "backup-src");
        var ws = await source.CreateWorkspaceAsync("Archive");
        var list = (await (await source.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var content = "%PDF-1.4\n% backup test\n%%EOF\n"u8.ToArray();
        var upload = await source.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents",
            new MultipartFormDataContent { { new ByteArrayContent(content), "file", "kept.pdf" } }, Ct);
        var item = (await upload.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        await source.CreateItemAsync(ws, list, new { fields = new { title = "Zeppelin manual" } });
        await Eventually.WaitForAsync<bool>(async () =>
            (await (await source.GetAsync("/v1.0/search?q=zeppelin", Ct)).ReadJsonAsync()).GetProperty("value").GetArrayLength() == 1 ? true : null);

        // The compatible organization database is independent of module storage and must travel with the archive.
        var canonicalWorkspaceResponse = await source.PostAsJsonAsync("/v1.0/content/workspaces", new { name = "Published records" }, Ct);
        canonicalWorkspaceResponse.EnsureSuccessStatusCode();
        var canonicalWorkspace = (await canonicalWorkspaceResponse.ReadJsonAsync()).GetProperty("id").GetString();
        var canonicalCollectionResponse = await source.PostAsJsonAsync($"/v1.0/content/workspaces/{canonicalWorkspace}/collections", new { name = "Records" }, Ct);
        canonicalCollectionResponse.EnsureSuccessStatusCode();
        var canonicalCollection = (await canonicalCollectionResponse.ReadJsonAsync()).GetProperty("id").GetString();
        var canonicalSchema = await (await source.GetAsync($"/v1.0/content/collections/{canonicalCollection}", Ct)).ReadJsonAsync();
        var canonicalType = canonicalSchema.GetProperty("schema").GetProperty("content_types")[0].GetProperty("id").GetString();
        var canonicalCreated = await source.PostAsJsonAsync($"/v1.0/content/resources/{canonicalCollection}/items", new { content = new { name = "Kept content", contentTypeId = canonicalType, values = new { } } }, Ct);
        canonicalCreated.EnsureSuccessStatusCode();
        var canonicalItem = (await canonicalCreated.ReadJsonAsync()).GetProperty("resource").GetProperty("id").GetString();

        var archive = Path.Combine(Path.GetTempPath(), $"pdn_backup_{Guid.NewGuid():N}.tar.gz");
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var result = await scope.ServiceProvider.GetRequiredService<BackupService>().BackupAsync(archive, Ct);
                Assert.True(result.Files >= 1);
            }

            var entries = await EntriesAsync(archive);
            Assert.Contains("manifest.json", entries);
            Assert.Contains(entries, e => e.StartsWith("folders/content-databases/", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.StartsWith("database.", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.StartsWith("blobs/", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, e => e.Contains("/renders/", StringComparison.Ordinal) || e.Contains(".tmp", StringComparison.Ordinal));

            await using var target = new PaperDotNetApiFactory();
            await target.InitializeAsync();
            await using (var scope = target.Services.CreateAsyncScope())
            {
                var backups = scope.ServiceProvider.GetRequiredService<BackupService>();

                // The fresh installation has its bootstrap tenant: replacing it needs --force.
                await Assert.ThrowsAsync<InvalidOperationException>(() => backups.RestoreAsync(archive, force: false, Ct));
                var manifest = await backups.RestoreAsync(archive, force: true, Ct);
                Assert.Equal(BackupService.Format, manifest.Format);
            }

            var restored = await ApiClient.CreateAsync(target, "backup-src");
            var restoredContentResponse = await restored.GetAsync($"/v1.0/content/items/{canonicalItem}", Ct);
            restoredContentResponse.EnsureSuccessStatusCode();
            var restoredContent = await restoredContentResponse.ReadJsonAsync();
            Assert.Equal("Kept content", restoredContent.GetProperty("resource").GetProperty("name").GetString());
            Assert.Equal(canonicalItem, restoredContent.GetProperty("resource").GetProperty("id").GetString());
            Assert.Equal(content, await restored.GetByteArrayAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file", Ct));
            var hits = await (await restored.GetAsync("/v1.0/search?q=zeppelin", Ct)).ReadJsonAsync();
            Assert.Equal(1, hits.GetProperty("value").GetArrayLength());
            Assert.Equal(HttpStatusCode.Unauthorized, (await (await ApiClient.CreateAsync(target, "backup-src", userName: null)).GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    private static async Task<List<string>> EntriesAsync(string archive)
    {
        var names = new List<string>();
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(cancellationToken: Ct) is { } entry)
        {
            names.Add(entry.Name);
        }

        return names;
    }
}
