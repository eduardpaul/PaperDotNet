using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Dav.Data;
using PaperDotNet.Dav.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>WebDAV writes (ADR-0047, WD-3): uploads and versions, folders, moves, deletes, copies, locks, temporary files.</summary>
public sealed partial class DavTests
{
    private const string Lib = "/dav/Projects/Contracts";

    private const string LockInfo = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:lockinfo xmlns:D="DAV:"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype><D:owner>tester</D:owner></D:lockinfo>
        """;

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string url, byte[] content, Action<HttpRequestMessage>? configure = null) =>
        SendAsync(client, "PUT", url, depth: null, r =>
        {
            r.Content = new ByteArrayContent(content);
            configure?.Invoke(r);
        });

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, string from, string to, bool overwrite = false) =>
        SendAsync(client, "MOVE", from, depth: null, r =>
        {
            r.Headers.Add("Destination", to);
            r.Headers.Add("Overwrite", overwrite ? "T" : "F");
        });

    private static async Task<string> LockAsync(HttpClient client, string url, string timeout = "Second-600")
    {
        var response = await SendAsync(client, "LOCK", url, depth: "0", r =>
        {
            r.Headers.Add("Timeout", timeout);
            r.Content = new StringContent(LockInfo, Encoding.UTF8, "application/xml");
        });
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return response.Headers.GetValues("Lock-Token").Single();
    }

    /// <summary>The library's items (documents and folders) by title.</summary>
    private static async Task<Dictionary<string, JsonElement>> ItemsAsync(Library library) =>
        (await (await library.Admin.GetAsync($"{library.Url}/items?$top=100", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .ToDictionary(i => i.GetProperty("fields").GetProperty("title").GetString()!, i => i);

    private static async Task<List<JsonElement>> VersionsAsync(Library library, Guid item) =>
        (await (await library.Admin.GetAsync($"{library.Url}/items/{item}/file/versions", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();

    private static Guid Id(JsonElement item) => item.GetProperty("id").GetGuid();

    [Fact]
    public async Task Put_uploads_documents_and_stores_new_versions()
    {
        var library = await SetupAsync("dav-put", WriteScopes);

        var created = await PutAsync(library.Dav, $"{Lib}/Report.docx", [0x50, 0x4B, 0x03, 0x04, 1]);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = (await ItemsAsync(library))["Report"];
        Assert.Equal(["Report.docx"], await NamesAsync(library.Dav, $"{Lib}/"));
        var props = (await PropFindAsync(library.Dav, $"{Lib}/Report.docx", "0")).Values.Single();
        Assert.Equal("00000020", props.Element(Ms + "Win32FileAttributes")!.Value);

        var etag = (await library.Dav.GetAsync($"{Lib}/Report.docx", Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(library.Dav, $"{Lib}/Report.docx", [0x50, 0x4B, 0x03, 0x04, 2], r => r.Headers.TryAddWithoutValidation("If-Match", etag))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutAsync(library.Dav, $"{Lib}/Report.docx", [3], r => r.Headers.TryAddWithoutValidation("If-Match", etag))).StatusCode);
        Assert.Equal(2, (await VersionsAsync(library, Id(report))).Count);
        Assert.Equal([0x50, 0x4B, 0x03, 0x04, 2], await library.Dav.GetByteArrayAsync($"{Lib}/Report.docx", Ct));

        // PDFs are processed like uploads; a missing parent folder is not found.
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, $"{Lib}/Scan.PDF", Pdf("scan"))).StatusCode);
        Assert.Equal("application/pdf", (await library.Dav.GetAsync($"{Lib}/Scan.PDF", Ct)).Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(library.Dav, $"{Lib}/Missing/x.txt", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(library.Dav, "/dav/Projects/Loose.txt", [1])).StatusCode);
    }

    [Fact]
    public async Task Explorer_creates_a_file_empty_then_fills_it_in_place()
    {
        var library = await SetupAsync("dav-explorer", WriteScopes);
        var url = $"{Lib}/Notes.txt";

        // Windows: an empty PUT, a LOCK, the content with the lock token, the Win32 times, UNLOCK.
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, url, [])).StatusCode);
        var token = await LockAsync(library.Dav, url);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(library.Dav, url, Encoding.UTF8.GetBytes("hello"), r => r.Headers.TryAddWithoutValidation("If", $"({token})"))).StatusCode);
        var patch = await SendAsync(library.Dav, "PROPPATCH", url, depth: null, r =>
        {
            r.Headers.TryAddWithoutValidation("If", $"({token})");
            r.Content = new StringContent("""
                <?xml version="1.0" encoding="utf-8"?>
                <D:propertyupdate xmlns:D="DAV:" xmlns:Z="urn:schemas-microsoft-com:"><D:set><D:prop>
                <Z:Win32CreationTime>Wed, 07 Oct 2026 09:00:00 GMT</Z:Win32CreationTime>
                <Z:Win32LastModifiedTime>Wed, 07 Oct 2026 09:00:00 GMT</Z:Win32LastModifiedTime>
                <Z:Win32FileAttributes>00000020</Z:Win32FileAttributes>
                </D:prop></D:set></D:propertyupdate>
                """, Encoding.UTF8, "application/xml");
        });
        Assert.Equal((HttpStatusCode)207, patch.StatusCode);
        Assert.DoesNotContain("403", await patch.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(library.Dav, "UNLOCK", url, depth: null, r => r.Headers.Add("Lock-Token", token))).StatusCode);

        var versions = await VersionsAsync(library, Id((await ItemsAsync(library))["Notes"]));
        var version = Assert.Single(versions);
        Assert.Equal(5, version.GetProperty("size").GetInt64());
        Assert.Equal("hello", await library.Dav.GetStringAsync(url, Ct));
    }

    [Fact]
    public async Task Folders_are_created_renamed_moved_and_deleted_and_files_keep_their_identity()
    {
        var library = await SetupAsync("dav-folders", WriteScopes);
        var archive = await library.Admin.PostAsJsonAsync($"/v1.0/workspaces/{library.Workspace}/lists", new { name = "Archive", templateKey = "documents" }, Ct);
        Assert.Equal(HttpStatusCode.Created, archive.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(library.Dav, "MKCOL", $"{Lib}/2026/", depth: null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(library.Dav, "MKCOL", $"{Lib}/2026/", depth: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(library.Dav, "MKCOL", "/dav/Projects/New library/", depth: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, $"{Lib}/2026/Lease.pdf", Pdf("lease"))).StatusCode);
        var lease = Id((await ItemsAsync(library))["Lease"]);

        // Rename a folder and a file; a PDF keeps its type, so its extension cannot change.
        Assert.Equal(HttpStatusCode.Created, (await MoveAsync(library.Dav, $"{Lib}/2026/", $"{Lib}/Year 2026/")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await MoveAsync(library.Dav, $"{Lib}/Year 2026/Lease.pdf", $"{Lib}/Year 2026/Rental lease.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await MoveAsync(library.Dav, $"{Lib}/Year 2026/Rental lease.pdf", $"{Lib}/Year 2026/Rental lease.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await MoveAsync(library.Dav, $"{Lib}/Year 2026/Rental lease.pdf", $"{Lib}/Year 2026/rental LEASE.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await MoveAsync(library.Dav, $"{Lib}/Year 2026/rental LEASE.pdf", $"{Lib}/Year 2026/Rental lease.pdf")).StatusCode);
        var items = await ItemsAsync(library);
        Assert.Equal(lease, Id(items["Rental lease"]));
        Assert.Equal(Id(items["Year 2026"]), items["Rental lease"].GetProperty("parentId").GetGuid());

        // Into another library: the same item with its versions.
        Assert.Equal(HttpStatusCode.Created, (await MoveAsync(library.Dav, $"{Lib}/Year 2026/Rental lease.pdf", "/dav/Projects/Archive/Rental lease.pdf")).StatusCode);
        Assert.Equal(["Rental lease.pdf"], await NamesAsync(library.Dav, "/dav/Projects/Archive/"));
        Assert.Empty(await NamesAsync(library.Dav, $"{Lib}/Year 2026/"));
        var archived = (await ItemsAsync(library with { List = (await archive.ReadJsonAsync()).GetProperty("id").GetGuid() }))["Rental lease"];
        Assert.Equal(lease, Id(archived));

        // Deleting a folder recycles it with its content.
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, $"{Lib}/Year 2026/Note.txt", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(library.Dav, "DELETE", $"{Lib}/Year 2026/", depth: null)).StatusCode);
        Assert.Empty(await NamesAsync(library.Dav, $"{Lib}/"));
        var bin = (await (await library.Admin.GetAsync($"{library.Url}/recycleBin", Ct)).ReadJsonAsync()).GetProperty("value").GetArrayLength();
        Assert.Equal(2, bin);
    }

    [Fact]
    public async Task Copies_are_new_documents_and_follow_the_duplicate_policy()
    {
        var library = await SetupAsync("dav-copy", WriteScopes);
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, $"{Lib}/Offer.txt", Encoding.UTF8.GetBytes("offer"))).StatusCode);

        var copy = await SendAsync(library.Dav, "COPY", $"{Lib}/Offer.txt", depth: null, r => r.Headers.Add("Destination", $"{Lib}/Offer copy.txt"));
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var items = await ItemsAsync(library);
        Assert.NotEqual(Id(items["Offer"]), Id(items["Offer copy"]));

        var settings = await library.Admin.PutAsJsonAsync($"{library.Url}/documentSettings", new { duplicatePolicy = "block" }, Ct);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var blocked = await SendAsync(library.Dav, "COPY", $"{Lib}/Offer.txt", depth: null, r => r.Headers.Add("Destination", $"{Lib}/Third.txt"));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
    }

    [Fact]
    public async Task Office_safe_save_stores_a_new_version_of_the_same_document()
    {
        var library = await SetupAsync("dav-office", WriteScopes);
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, $"{Lib}/Report.docx", Encoding.UTF8.GetBytes("v1"))).StatusCode);
        var report = Id((await ItemsAsync(library))["Report"]);

        // Word on a mapped drive: owner file, new content into a temporary file, original renamed away, temporary file
        // renamed to the original name, renamed original deleted.
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, $"{Lib}/~$Report.docx", [1, 2])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, $"{Lib}/~WRD0001.tmp", Encoding.UTF8.GetBytes("v2"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await MoveAsync(library.Dav, $"{Lib}/Report.docx", $"{Lib}/~WRL0002.tmp")).StatusCode);
        Assert.Equal(["~$Report.docx", "~WRD0001.tmp", "~WRL0002.tmp"], await NamesAsync(library.Dav, $"{Lib}/"));
        Assert.Equal("v1", await library.Dav.GetStringAsync($"{Lib}/~WRL0002.tmp", Ct));
        Assert.Equal(HttpStatusCode.Created, (await MoveAsync(library.Dav, $"{Lib}/~WRD0001.tmp", $"{Lib}/Report.docx")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(library.Dav, "DELETE", $"{Lib}/~WRL0002.tmp", depth: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(library.Dav, "DELETE", $"{Lib}/~$Report.docx", depth: null)).StatusCode);

        Assert.Equal(["Report.docx"], await NamesAsync(library.Dav, $"{Lib}/"));
        Assert.Equal("v2", await library.Dav.GetStringAsync($"{Lib}/Report.docx", Ct));
        var items = await ItemsAsync(library);
        Assert.Equal(report, Id(Assert.Single(items).Value));
        Assert.Equal(2, (await VersionsAsync(library, report)).Count);

        // Temporary files are the writer's own.
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, $"{Lib}/~$Other.docx", [1])).StatusCode);
        var user = await library.Admin.PostAsJsonAsync("/v1.0/users", new { userName = "mia", password = "mia-password-1" }, Ct);
        await library.Admin.PostAsJsonAsync($"/v1.0/workspaces/{library.Workspace}/members", new { userId = (await user.ReadJsonAsync()).GetProperty("id").GetGuid(), role = "member" }, Ct);
        var mia = await DavClientAsync(await ApiClient.CreateAsync(factory, library.Tenant, "mia", "mia-password-1"), library.Tenant, WriteScopes);
        Assert.Equal(["Report.docx"], await NamesAsync(mia, $"{Lib}/"));

        // Visitors (readers) keep no temporary files, and cannot hide a document by renaming it.
        var reader = await library.Admin.PostAsJsonAsync("/v1.0/users", new { userName = "rey", password = "rey-password-1" }, Ct);
        await library.Admin.PostAsJsonAsync($"/v1.0/workspaces/{library.Workspace}/members", new { userId = (await reader.ReadJsonAsync()).GetProperty("id").GetGuid(), role = "visitor" }, Ct);
        var rey = await DavClientAsync(await ApiClient.CreateAsync(factory, library.Tenant, "rey", "rey-password-1"), library.Tenant, WriteScopes);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(rey, $"{Lib}/~$Report.docx", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await MoveAsync(rey, $"{Lib}/Report.docx", $"{Lib}/~WRL0003.tmp")).StatusCode);
        Assert.Equal(["Report.docx"], await NamesAsync(rey, $"{Lib}/"));

        // A temporary file larger than WebDav:MaxTransientFileSize is refused and not kept.
        var tooLarge = await PutAsync(library.Dav, $"{Lib}/~WRD0009.tmp", new byte[PaperDotNetApiFactory.DocumentLimit + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.DoesNotContain("~WRD0009.tmp", await NamesAsync(library.Dav, $"{Lib}/"));
    }

    [Fact]
    public async Task Locks_keep_others_out_until_released_and_expire()
    {
        var library = await SetupAsync("dav-locks", WriteScopes);
        var url = $"{Lib}/Budget.xlsx";
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, url, [0x50, 0x4B, 0x03, 0x04])).StatusCode);
        var user = await library.Admin.PostAsJsonAsync("/v1.0/users", new { userName = "lou", password = "lou-password-1" }, Ct);
        await library.Admin.PostAsJsonAsync($"/v1.0/workspaces/{library.Workspace}/members", new { userId = (await user.ReadJsonAsync()).GetProperty("id").GetGuid(), role = "member" }, Ct);
        var lou = await DavClientAsync(await ApiClient.CreateAsync(factory, library.Tenant, "lou", "lou-password-1"), library.Tenant, WriteScopes);

        // A long or infinite timeout is shortened to WebDav:MaxLockTimeout (1 hour).
        var token = await LockAsync(library.Dav, url, "Infinite, Second-4100000000");
        var discovery = XDocument.Parse(await (await SendAsync(library.Dav, "PROPFIND", url, "0")).Content.ReadAsStringAsync(Ct));
        Assert.Equal("Second-3600", discovery.Descendants(D + "timeout").Single().Value);

        Assert.Equal((HttpStatusCode)423, (await PutAsync(lou, url, [1])).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await SendAsync(lou, "DELETE", url, depth: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(library.Dav, url, [0x50, 0x4B, 0x03, 0x04, 9], r => r.Headers.TryAddWithoutValidation("If", $"({token})"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(library.Dav, "UNLOCK", url, depth: null, r => r.Headers.Add("Lock-Token", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(lou, url, [0x50, 0x4B, 0x03, 0x04, 10])).StatusCode);

        // Expired locks no longer count, and dav.cleanup removes them with expired temporary files.
        await LockAsync(lou, url);
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(lou, $"{Lib}/~$Budget.xlsx", [1])).StatusCode);
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(library.TenantId, library.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DavDbContext>();
            await db.Locks.ExecuteUpdateAsync(l => l.SetProperty(x => x.Expiration, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
            Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(library.Dav, url, [0x50, 0x4B, 0x03, 0x04, 11])).StatusCode);

            await db.TransientFiles.ExecuteUpdateAsync(f => f.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
            var blob = (await db.TransientFiles.SingleAsync(Ct)).BlobKey!;
            var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
            Assert.True(await blobs.ExistsAsync(blob, Ct));
            await ActivatorUtilities.CreateInstance<DavCleanupJob>(scope.ServiceProvider).RunAsync(Ct);
            Assert.Equal(0, await db.Locks.CountAsync(Ct));
            Assert.Equal(0, await db.TransientFiles.CountAsync(Ct));
            Assert.False(await blobs.ExistsAsync(blob, Ct));
        }
    }

    [Fact]
    public async Task Locks_and_temporary_files_stay_in_their_tenant()
    {
        var first = await SetupAsync("dav-locks-a", WriteScopes);
        var second = await SetupAsync("dav-locks-b", WriteScopes);
        foreach (var library in new[] { first, second })
        {
            Assert.Equal(HttpStatusCode.Created, (await PutAsync(library.Dav, $"{Lib}/Plan.txt", [1])).StatusCode);
        }

        await LockAsync(first.Dav, $"{Lib}/Plan.txt");
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(first.Dav, $"{Lib}/~$Plan.txt", [1])).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(second.Dav, $"{Lib}/Plan.txt", [2])).StatusCode);
        Assert.Equal(["Plan.txt"], await NamesAsync(second.Dav, $"{Lib}/"));
    }
}
