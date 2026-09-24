using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Documents.Features;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Documents, slice 3a: upload, type detection, file versions, duplicates, storage (DOC-01…03, DOC-10, DOC-11, DOC-15).</summary>
public sealed class DocumentTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static async Task<(Guid Workspace, Guid Library)> LibraryAsync(HttpClient client)
    {
        var ws = await client.CreateWorkspaceAsync("Records");
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (ws, (await response.ReadJsonAsync()).GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid ws, Guid list, byte[] content, string fileName, string? title = null) =>
        client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", Form(content, fileName, title), Ct);

    private static async Task<JsonElement> UploadOkAsync(HttpClient client, Guid ws, Guid list, byte[] content, string fileName)
    {
        var response = await UploadAsync(client, ws, list, content, fileName);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadJsonAsync();
    }

    [Fact]
    public async Task Upload_download_replace_and_restore_keep_every_version()
    {
        await factory.CreateTenantAsync("doc-basics");
        var client = await ApiClient.CreateAsync(factory, "doc-basics");
        var (ws, list) = await LibraryAsync(client);
        var original = Pdf("original");

        var created = await UploadOkAsync(client, ws, list, original, "C:\\scans\\Invoice 42.pdf");
        var item = created.GetProperty("itemId").GetGuid();
        var file = created.GetProperty("file");
        Assert.Equal("Invoice 42", created.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("Invoice 42.pdf", file.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", file.GetProperty("mediaType").GetString());
        Assert.Equal(original.Length, file.GetProperty("size").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), file.GetProperty("sha256").GetString());

        var fileUrl = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";
        var download = await client.GetAsync(fileUrl, Ct);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(original, await download.Content.ReadAsByteArrayAsync(Ct));
        var etag = download.Headers.ETag!.Tag;

        using var range = new HttpRequestMessage(HttpMethod.Get, fileUrl);
        range.Headers.Range = new RangeHeaderValue(0, 3);
        var partial = await client.SendAsync(range, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(await partial.Content.ReadAsByteArrayAsync(Ct)));

        // A PNG named .pdf: the content decides the type and the extension.
        using (var stale = new HttpRequestMessage(HttpMethod.Put, fileUrl) { Content = Form(PngBytes, "scan.pdf") })
        {
            stale.Headers.TryAddWithoutValidation("If-Match", "\"0000\"");
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale, Ct)).StatusCode);
        }

        using var replace = new HttpRequestMessage(HttpMethod.Put, fileUrl) { Content = Form(PngBytes, "scan.pdf") };
        replace.Headers.TryAddWithoutValidation("If-Match", etag);
        var replaced = await client.SendAsync(replace, Ct);
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, await replaced.Content.ReadAsStringAsync(Ct));
        var second = (await replaced.ReadJsonAsync()).GetProperty("file");
        Assert.Equal(2, second.GetProperty("number").GetInt32());
        Assert.Equal("image/png", second.GetProperty("mediaType").GetString());
        Assert.Equal("scan.png", second.GetProperty("fileName").GetString());

        var restored = await client.PostAsync($"{fileUrl}/versions/1/restore", null, Ct);
        Assert.Equal(3, (await restored.ReadJsonAsync()).GetProperty("number").GetInt32());

        var versions = (await (await client.GetAsync($"{fileUrl}/versions", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal([3, 2, 1], versions.Select(v => v.GetProperty("number").GetInt32()));
        Assert.Equal([true, false, false], versions.Select(v => v.GetProperty("isCurrent").GetBoolean()));
        Assert.Equal(original, await client.GetByteArrayAsync(fileUrl, Ct));
        Assert.Equal(PngBytes, await client.GetByteArrayAsync($"{fileUrl}/versions/2", Ct));
    }

    [Fact]
    public async Task Uploads_are_checked()
    {
        await factory.CreateTenantAsync("doc-checks");
        var client = await ApiClient.CreateAsync(factory, "doc-checks");
        var (ws, library) = await LibraryAsync(client);
        var plainList = await client.CreateListAsync(ws, "Plain");

        var text = await UploadAsync(client, ws, library, Encoding.UTF8.GetBytes("just text"), "fake.pdf");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        Assert.Equal("unsupportedFileType", (await text.ReadJsonAsync()).GetProperty("code").GetString());

        var notLibrary = await UploadAsync(client, ws, plainList, Pdf("x"), "x.pdf");
        Assert.Equal("notALibrary", (await notLibrary.ReadJsonAsync()).GetProperty("code").GetString());

        var tooLarge = new byte[PaperDotNetApiFactory.DocumentLimit + 10];
        Pdf("big").CopyTo(tooLarge, 0);
        var large = await UploadAsync(client, ws, library, tooLarge, "big.pdf");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);

        var noFile = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{library}/documents", new MultipartFormDataContent { { new StringContent("t"), "title" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
    }

    [Fact]
    public async Task Duplicates_are_reported_or_blocked_and_stored_once()
    {
        var tenant = await factory.CreateTenantAsync("doc-dupes");
        var client = await ApiClient.CreateAsync(factory, "doc-dupes");
        var (ws, list) = await LibraryAsync(client);
        var content = Pdf("same");

        var first = await UploadOkAsync(client, ws, list, content, "a.pdf");
        var second = await UploadOkAsync(client, ws, list, content, "b.pdf");
        var duplicate = Assert.Single(second.GetProperty("duplicates").EnumerateArray());
        Assert.Equal(first.GetProperty("itemId").GetGuid(), duplicate.GetProperty("itemId").GetGuid());

        var settingsUrl = $"/v1.0/workspaces/{ws}/lists/{list}/documentSettings";
        Assert.Equal("warn", (await (await client.GetAsync(settingsUrl, Ct)).ReadJsonAsync()).GetProperty("duplicatePolicy").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(settingsUrl, new { duplicatePolicy = "block" }, Ct)).StatusCode);
        var blocked = await UploadAsync(client, ws, list, content, "c.pdf");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("duplicateFile", (await blocked.ReadJsonAsync()).GetProperty("code").GetString());

        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        Assert.Equal(1, await db.StoredFiles.CountAsync(Ct));
        Assert.Equal(2, await db.FileVersions.CountAsync(Ct));
    }

    [Fact]
    public async Task Documents_are_isolated_by_tenant_and_permissions()
    {
        await factory.CreateTenantAsync("doc-iso-a");
        await factory.CreateTenantAsync("doc-iso-b");
        var a = await ApiClient.CreateAsync(factory, "doc-iso-a");
        var b = await ApiClient.CreateAsync(factory, "doc-iso-b");
        var (ws, list) = await LibraryAsync(a);
        var content = Pdf("secret");
        var item = (await UploadOkAsync(a, ws, list, content, "secret.pdf")).GetProperty("itemId").GetGuid();
        var fileUrl = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(fileUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"{fileUrl}/versions", Ct)).StatusCode);
        var (wsB, listB) = await LibraryAsync(b);
        var inB = await UploadOkAsync(b, wsB, listB, content, "same.pdf");
        Assert.Equal(0, inB.GetProperty("duplicates").GetArrayLength());

        // A member of the tenant who is not in the workspace sees nothing, and cannot change library settings.
        await a.PostAsJsonAsync("/v1.0/users", new { userName = "outsider", password = "outsider-password-1" }, Ct);
        var outsider = await ApiClient.CreateAsync(factory, "doc-iso-a", "outsider", "outsider-password-1");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(fileUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(outsider, ws, list, Pdf("o"), "o.pdf")).StatusCode);
        var settings = await outsider.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/documentSettings", new { duplicatePolicy = "allow" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, settings.StatusCode);
    }

    [Fact]
    public async Task Uploads_to_the_inbox_land_in_the_home_workspace()
    {
        await factory.CreateTenantAsync("doc-inbox");
        var client = await ApiClient.CreateAsync(factory, "doc-inbox");

        var response = await client.PostAsync("/v1.0/me/inbox/documents", Form(Pdf("inbox"), "scan.pdf", "Scan from the copier"), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var document = await response.ReadJsonAsync();
        var home = await (await client.GetAsync("/v1.0/me/home", Ct)).ReadJsonAsync();

        Assert.Equal(home.GetProperty("inboxListId").GetGuid(), document.GetProperty("listId").GetGuid());
        Assert.Equal("Scan from the copier", document.GetProperty("fields").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Purged_items_release_their_content()
    {
        var tenant = await factory.CreateTenantAsync("doc-purge");
        var client = await ApiClient.CreateAsync(factory, "doc-purge");
        var (ws, list) = await LibraryAsync(client);
        var created = await UploadOkAsync(client, ws, list, Pdf("gone"), "gone.pdf");
        var item = created.GetProperty("itemId").GetGuid();
        var itemUrl = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}";
        var etag = (await client.GetAsync(itemUrl, Ct)).Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendWithEtagAsync(HttpMethod.Delete, itemUrl, etag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{itemUrl}/file", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/v1.0/workspaces/{ws}/lists/{list}/recycleBin/{item}", Ct)).StatusCode);

        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        await Eventually.WaitForAsync<bool>(async () => await db.FileVersions.AnyAsync(v => v.ItemId == item, Ct) ? null : true);

        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var stored = await db.StoredFiles.SingleAsync(Ct);
        Assert.True(await blobs.ExistsAsync(stored.BlobKey, Ct));
        await new StoredFileCleanupJob(db, blobs, new ShiftedTime(TimeSpan.FromHours(2))).RunAsync(Ct);

        Assert.False(await blobs.ExistsAsync(stored.BlobKey, Ct));
        Assert.Equal(0, await db.StoredFiles.CountAsync(Ct));
    }

    private sealed class ShiftedTime(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
