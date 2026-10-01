using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using PaperDotNet.Host.Cli;

namespace PaperDotNet.IntegrationTests;

/// <summary>Admin commands of the server binary (PLT-12 backup and restore, PLT-13 export and import, tenants and users).</summary>
public sealed class AdminCliTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pdn-cli-tests", Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(TestHost host, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await AdminCli.RunAsync(host.Services, args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<(string Workspace, string Library, string Item, byte[] Content)> DocumentAsync(HttpClient client)
    {
        var ws = await Api.CreateWorkspaceAsync(client, "Archive");
        using var library = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        var list = (await library.JsonAsync(HttpStatusCode.Created)).Id();
        var content = "%PDF-1.4\n% zeppelin manual\n%%EOF\n"u8.ToArray();
        using var upload = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents",
            new MultipartFormDataContent { { new ByteArrayContent(content), "file", "zeppelin.pdf" } }, Ct);
        return (ws, list, (await upload.JsonAsync(HttpStatusCode.Created)).GetProperty("itemId").GetString()!, content);
    }

    /// <summary>The entries of a collection response (a plain array or a page).</summary>
    private static JsonElement.ArrayEnumerator Values(JsonElement response) =>
        (response.ValueKind == JsonValueKind.Array ? response : response.GetProperty("value")).EnumerateArray();

    [Fact]
    public async Task Backup_and_restore_move_an_installation()
    {
        var source = await _host.CreateTenantAsync("backup-src");
        var (ws, list, item, content) = await DocumentAsync(source);
        var archive = Path.Combine(_folder, "backup.tar.gz");

        var (code, output, _) = await RunAsync(_host, "backup", "-o", archive);
        Assert.True(code == 0, output);
        var entries = new List<string>();
        await using (var file = File.OpenRead(archive))
        await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        await using (var reader = new TarReader(gzip))
        {
            while (await reader.GetNextEntryAsync(cancellationToken: Ct) is { } entry)
            {
                entries.Add(entry.Name);
            }
        }

        Assert.Equal("manifest.json", entries[0]);
        Assert.Contains("database.sqlite", entries);
        Assert.Contains(entries, e => e.StartsWith("blobs/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Contains("/renders/", StringComparison.Ordinal) || e.Contains(".tmp/", StringComparison.Ordinal));

        await using var target = new TestHost();
        _ = target.Services;

        // The fresh installation has its bootstrap tenant: replacing it needs --force.
        var refused = await RunAsync(target, "restore", archive);
        Assert.Equal(1, refused.Code);
        Assert.Contains("--force", refused.Error, StringComparison.Ordinal);
        var restored = await RunAsync(target, "restore", archive, "--force");
        Assert.True(restored.Code == 0, restored.Error);

        var client = await target.SignInAsync(tenant: "backup-src");
        Assert.Equal(content, await client.GetByteArrayAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file", Ct));
        var notABackup = Path.Combine(_folder, "other.tar.gz");
        await File.WriteAllBytesAsync(notABackup, Encoding.UTF8.GetBytes("nope"), Ct);
        Assert.Equal(1, (await RunAsync(target, "restore", notABackup, "--force")).Code);
    }

    [Fact]
    public async Task Export_and_import_move_a_tenant_with_its_files()
    {
        var source = await _host.CreateTenantAsync("export-src");
        var (_, _, _, content) = await DocumentAsync(source);
        var package = Path.Combine(_folder, "export.zip");

        Assert.Equal(1, (await RunAsync(_host, "export", "--tenant", "export-src", "--user", "nobody", "-o", package)).Code);
        var (code, output, error) = await RunAsync(_host, "export", "--tenant", "export-src", "--user", "admin", "-o", package);
        Assert.True(code == 0, error);
        Assert.Contains("Package written", output, StringComparison.Ordinal);

        await _host.CreateTenantAsync("import-dst");
        var dryRun = await RunAsync(_host, "import", package, "--tenant", "import-dst", "--user", "admin", "--dry-run");
        Assert.True(dryRun.Code == 0, dryRun.Error);
        Assert.Contains("Dry run", dryRun.Output, StringComparison.Ordinal);
        var imported = await RunAsync(_host, "import", package, "--tenant", "import-dst", "--user", "admin");
        Assert.True(imported.Code == 0, imported.Error);

        var client = await _host.SignInAsync(tenant: "import-dst");
        var workspaces = await (await client.GetAsync("/v1.0/workspaces", Ct)).JsonAsync(HttpStatusCode.OK);
        var ws = Assert.Single(Values(workspaces), w => w.GetProperty("name").GetString() == "Archive").Id();
        var lists = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists", Ct)).JsonAsync(HttpStatusCode.OK);
        var list = Assert.Single(Values(lists), l => l.GetProperty("name").GetString() == "Documents").Id();
        var items = await (await client.GetAsync(Api.Items(ws, list), Ct)).JsonAsync(HttpStatusCode.OK);
        var item = Assert.Single(Values(items)).Id();
        Assert.Equal(content, await client.GetByteArrayAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file", Ct));
    }

    [Fact]
    public async Task Tenants_users_and_the_search_index_are_managed_from_the_command_line()
    {
        var created = await RunAsync(_host, "tenant", "create", "--identifier", "acme", "--name", "Acme", "--admin-password", "Acme-Admin-123");
        Assert.True(created.Code == 0, created.Error);
        Assert.Contains("acme", (await RunAsync(_host, "tenant", "list")).Output, StringComparison.Ordinal);

        var user = await RunAsync(_host, "user", "create", "--tenant", "acme", "--username", "bob", "--password", "Bob-Pass-1234");
        Assert.True(user.Code == 0, user.Error);
        var bob = await _host.SignInAsync("bob", "Bob-Pass-1234", "acme");
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/v1.0/me", Ct)).StatusCode);
        Assert.Equal(1, (await RunAsync(_host, "user", "create", "--tenant", "missing", "--username", "x")).Code);

        Assert.Equal(0, (await RunAsync(_host, "tenant", "suspend", "acme")).Code);
        using (var token = await _host.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = "bob",
            ["password"] = "Bob-Pass-1234",
            ["tenant"] = "acme",
        }), Ct))
        {
            Assert.False(token.IsSuccessStatusCode);
        }

        Assert.Equal(0, (await RunAsync(_host, "tenant", "activate", "acme")).Code);
        var reindexed = await RunAsync(_host, "reindex", "--tenant", "acme");
        Assert.True(reindexed.Code == 0, reindexed.Error);
        Assert.Contains("acme: reindexed", reindexed.Output, StringComparison.Ordinal);
        Assert.Equal(1, (await RunAsync(_host, "reindex", "--tenant", "missing")).Code);
        Assert.Contains("Database is up to date", (await RunAsync(_host, "migrate")).Output, StringComparison.Ordinal);
    }
}
