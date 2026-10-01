using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Documents.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Documents core (T15a): upload, type detection, file versions, duplicates, storage, purge (DOC-01…03, DOC-10, DOC-11).</summary>
public sealed class DocumentTests : IAsyncLifetime
{
    private const int MaxFileSize = 64 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new(settings: new Dictionary<string, string> { ["Documents:MaxFileSize"] = MaxFileSize.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    private HttpClient _admin = null!;
    private Guid _tenant;

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _tenant = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static byte[] Pdf(string marker) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {marker}\n%%EOF\n");

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static MultipartFormDataContent Form(byte[] content, string fileName, string? title = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        return form;
    }

    private static async Task<(string Workspace, string Library)> LibraryAsync(HttpClient client)
    {
        var ws = await Api.CreateWorkspaceAsync(client, "Records");
        using var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        return (ws, (await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string ws, string list, byte[] content, string fileName, string? title = null) =>
        client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", Form(content, fileName, title), Ct);

    private static async Task<JsonElement> UploadOkAsync(HttpClient client, string ws, string list, byte[] content, string fileName)
    {
        using var response = await UploadAsync(client, ws, list, content, fileName);
        return await response.JsonAsync(HttpStatusCode.Created);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)).TryGetProperty("code", out var code) ? code.GetString() : null;

    [Fact]
    public async Task Upload_download_replace_and_restore_keep_every_version()
    {
        var (ws, list) = await LibraryAsync(_admin);
        var original = Pdf("original");

        var created = await UploadOkAsync(_admin, ws, list, original, "C:\\scans\\Invoice 42.pdf");
        var item = created.GetProperty("itemId").GetString();
        var file = created.GetProperty("file");
        Assert.Equal("Invoice 42", created.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("Invoice 42.pdf", file.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", file.GetProperty("mediaType").GetString());
        Assert.Equal(original.Length, file.GetProperty("size").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), file.GetProperty("sha256").GetString());

        var fileUrl = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";
        using var download = await _admin.GetAsync(fileUrl, Ct);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(original, await download.Content.ReadAsByteArrayAsync(Ct));
        var etag = download.Headers.ETag!.Tag;

        using var range = new HttpRequestMessage(HttpMethod.Get, fileUrl);
        range.Headers.Range = new RangeHeaderValue(0, 3);
        using var partial = await _admin.SendAsync(range, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(await partial.Content.ReadAsByteArrayAsync(Ct)));

        // A PNG named .pdf: the content decides the type and the extension.
        using (var stale = new HttpRequestMessage(HttpMethod.Put, fileUrl) { Content = Form(PngBytes, "scan.pdf") })
        {
            stale.Headers.TryAddWithoutValidation("If-Match", "\"0000\"");
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await _admin.SendAsync(stale, Ct)).StatusCode);
        }

        using var replace = new HttpRequestMessage(HttpMethod.Put, fileUrl) { Content = Form(PngBytes, "scan.pdf") };
        replace.Headers.TryAddWithoutValidation("If-Match", etag);
        using var replaced = await _admin.SendAsync(replace, Ct);
        var second = (await replaced.JsonAsync(HttpStatusCode.OK)).GetProperty("file");
        Assert.Equal(2, second.GetProperty("number").GetInt32());
        Assert.Equal("image/png", second.GetProperty("mediaType").GetString());
        Assert.Equal("scan.png", second.GetProperty("fileName").GetString());

        using var restored = await _admin.PostAsync($"{fileUrl}/versions/1/restore", null, Ct);
        Assert.Equal(3, (await restored.JsonAsync(HttpStatusCode.OK)).GetProperty("number").GetInt32());

        var versions = (await (await _admin.GetAsync($"{fileUrl}/versions", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal([3, 2, 1], versions.Select(v => v.GetProperty("number").GetInt32()));
        Assert.Equal([true, false, false], versions.Select(v => v.GetProperty("isCurrent").GetBoolean()));
        Assert.Equal(original, await _admin.GetByteArrayAsync(fileUrl, Ct));
        Assert.Equal(PngBytes, await _admin.GetByteArrayAsync($"{fileUrl}/versions/2", Ct));
    }

    [Fact]
    public async Task Uploads_are_checked()
    {
        var (ws, library) = await LibraryAsync(_admin);
        var plainList = (await Api.CreateListAsync(_admin, ws, "Plain")).Id();

        using var text = await UploadAsync(_admin, ws, library, Encoding.UTF8.GetBytes("just text"), "fake.pdf");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        Assert.Equal("unsupportedFileType", await CodeAsync(text));

        using var notLibrary = await UploadAsync(_admin, ws, plainList, Pdf("x"), "x.pdf");
        Assert.Equal("notALibrary", await CodeAsync(notLibrary));

        var tooLarge = new byte[MaxFileSize + 10];
        Pdf("big").CopyTo(tooLarge, 0);
        using var large = await UploadAsync(_admin, ws, library, tooLarge, "big.pdf");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);

        using var noFile = await _admin.PostAsync($"/v1.0/workspaces/{ws}/lists/{library}/documents", new MultipartFormDataContent { { new StringContent("t"), "title" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);

        using var badLanguages = await _admin.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{library}/documentSettings", new { ocrLanguages = "German!" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badLanguages.StatusCode);
    }

    [Fact]
    public async Task Duplicates_are_reported_or_blocked_and_stored_once()
    {
        var (ws, list) = await LibraryAsync(_admin);
        var content = Pdf("same");

        var first = await UploadOkAsync(_admin, ws, list, content, "a.pdf");
        var second = await UploadOkAsync(_admin, ws, list, content, "b.pdf");
        var duplicate = Assert.Single(second.GetProperty("duplicates").EnumerateArray());
        Assert.Equal(first.GetProperty("itemId").GetString(), duplicate.GetProperty("itemId").GetString());

        var settingsUrl = $"/v1.0/workspaces/{ws}/lists/{list}/documentSettings";
        var settings = await (await _admin.GetAsync(settingsUrl, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("warn", settings.GetProperty("duplicatePolicy").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync(settingsUrl, new { duplicatePolicy = "never" }, Ct)).StatusCode);
        using (var blocking = await _admin.SendAsync(Api.WithETag(HttpMethod.Put, settingsUrl, new { duplicatePolicy = "block" }, settings.ETag()), Ct))
        {
            Assert.Equal("block", (await blocking.JsonAsync(HttpStatusCode.OK)).GetProperty("duplicatePolicy").GetString());
        }

        Assert.Equal(HttpStatusCode.PreconditionFailed,
            (await _admin.SendAsync(Api.WithETag(HttpMethod.Put, settingsUrl, new { duplicatePolicy = "allow" }, settings.ETag()), Ct)).StatusCode);
        using var blocked = await UploadAsync(_admin, ws, list, content, "c.pdf");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("duplicateFile", await CodeAsync(blocked));

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var tenant = _tenant;
        Assert.Equal(1, await db.StoredFiles.CountAsync(f => f.TenantId == tenant, Ct));
        Assert.Equal(2, await db.FileVersions.CountAsync(v => v.TenantId == tenant, Ct));
    }

    [Fact]
    public async Task Documents_are_isolated_by_tenant_and_permissions()
    {
        var (ws, list) = await LibraryAsync(_admin);
        var content = Pdf("secret");
        var item = (await UploadOkAsync(_admin, ws, list, content, "secret.pdf")).GetProperty("itemId").GetString();
        var fileUrl = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";

        var other = await _host.CreateTenantAsync("documents-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(fileUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{fileUrl}/versions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{fileUrl}/versions/1/restore", null, Ct)).StatusCode);
        var (wsB, listB) = await LibraryAsync(other);
        Assert.Equal(0, (await UploadOkAsync(other, wsB, listB, content, "same.pdf")).GetProperty("duplicates").GetArrayLength());

        // A member of the tenant who is not in the workspace sees nothing, and cannot change library settings.
        using (var created = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = "outsider", password = "outsider-password-1" }, Ct))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var outsider = await _host.SignInAsync("outsider", "outsider-password-1");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(fileUrl, Ct)).StatusCode);
        using (var upload = await UploadAsync(outsider, ws, list, Pdf("o"), "o.pdf"))
        {
            Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/documentSettings", new { duplicatePolicy = "allow" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Purged_items_release_their_content()
    {
        var (ws, list) = await LibraryAsync(_admin);
        var item = (await UploadOkAsync(_admin, ws, list, Pdf("gone"), "gone.pdf")).GetProperty("itemId").GetString();
        var itemUrl = $"{Api.Items(ws, list)}/{item}";
        var etag = (await _admin.GetAsync(itemUrl, Ct)).Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.SendAsync(Api.WithETag(HttpMethod.Delete, itemUrl, null, etag), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"{itemUrl}/file", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/workspaces/{ws}/lists/{list}/recycleBin/{item}", Ct)).StatusCode);

        var itemId = Guid.Parse(item!);
        var tenant = _tenant;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var check = _host.Services.CreateAsyncScope();
            if (!await check.ServiceProvider.GetRequiredService<DocumentsDbContext>().FileVersions.AnyAsync(v => v.TenantId == tenant && v.ItemId == itemId, Ct))
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var stored = await db.StoredFiles.SingleAsync(f => f.TenantId == tenant, Ct);
        Assert.True(await blobs.ExistsAsync(stored.BlobKey, Ct));
        await new StoredFileCleanupJob(db, blobs, new ShiftedTime(TimeSpan.FromHours(2))).RunAsync(tenant, Ct);

        Assert.False(await blobs.ExistsAsync(stored.BlobKey, Ct));
        Assert.Equal(0, await db.StoredFiles.CountAsync(f => f.TenantId == tenant, Ct));
    }

    private sealed class ShiftedTime(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
