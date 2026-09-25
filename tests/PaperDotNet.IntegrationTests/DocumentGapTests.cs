using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>Slice 7f: fields on folders (LST-19), group inboxes (DOC-16), languages per file (DOC-17), page operations (DOC-05/06).</summary>
public sealed class DocumentGapTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A PDF with one text page per line.</summary>
    private static byte[] Pages(params string[] texts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in texts)
        {
            builder.AddPage(PageSize.A4).AddText(text, 24, new PdfPoint(40, 760), font);
        }

        return builder.Build();
    }

    private static async Task<(Guid Workspace, Guid Library)> LibraryAsync(HttpClient client, string name)
    {
        var ws = await client.CreateWorkspaceAsync(name);
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        return (ws, (await response.ReadJsonAsync()).GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> UploadAsync(HttpClient client, string url, byte[] content, string fileName, string? languages = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        if (languages is not null)
        {
            form.Add(new StringContent(languages), "languages");
        }

        var response = await client.PostAsync(url, form, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadJsonAsync();
    }

    private static string Item(Guid ws, Guid list, Guid item) => $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}";

    private static async Task<List<JsonElement>> ProcessedAsync(HttpClient client, string itemUrl)
    {
        List<JsonElement> versions = [];
        await Eventually.WaitForAsync<bool>(async () =>
        {
            var body = await (await client.GetAsync($"{itemUrl}/file/versions", Ct)).ReadJsonAsync();
            versions = [.. body.GetProperty("value").EnumerateArray()];
            return versions.All(v => v.GetProperty("processingStatus").GetString() is "succeeded" or "failed") ? true : null;
        }, TimeSpan.FromSeconds(90));
        return versions;
    }

    private static async Task<List<(string Text, int Rotation)>> DownloadPagesAsync(HttpClient client, string itemUrl)
    {
        var bytes = await (await client.GetAsync($"{itemUrl}/file", Ct)).Content.ReadAsByteArrayAsync(Ct);
        using var pdf = PdfDocument.Open(bytes);
        return [.. pdf.GetPages().Select(p => (p.Text.Trim(), p.Rotation.Value))];
    }

    [Fact]
    public async Task Folders_hold_optional_field_values()
    {
        await factory.CreateTenantAsync("gaps-folders");
        var client = await ApiClient.CreateAsync(factory, "gaps-folders");
        var ws = await client.CreateWorkspaceAsync("Folders");
        var type = await client.CreateContentTypeAsync("Case", [
            new { name = "tags", type = "keywords", allowMultiple = true },
            new { name = "amount", type = "number", required = true },
        ]);
        var list = await client.CreateListAsync(ws, "Cases", type);
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items";

        var folder = await client.PostAsJsonAsync(url, new { isFolder = true, fields = new { title = "2026", tags = new[] { "archive" } } }, Ct);
        Assert.Equal(HttpStatusCode.Created, folder.StatusCode);
        var created = await folder.ReadJsonAsync();
        Assert.Equal(1, created.GetProperty("fields").GetProperty("tags").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(url, new { isFolder = true, fields = new { title = "Bad", amount = "many" } }, Ct)).StatusCode);

        // Items still need their required fields.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { fields = new { title = "No amount" } }, Ct)).StatusCode);

        var id = created.GetProperty("id").GetGuid();
        var etag = (await client.GetAsync($"{url}/{id}", Ct)).Headers.ETag!.Tag;
        var updated = await client.SendWithEtagAsync(HttpMethod.Patch, $"{url}/{id}", etag, new { fields = new { tags = new[] { "archive", "2026" } } });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await updated.ReadJsonAsync()).GetProperty("fields").GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public async Task Pages_are_edited_extracted_and_moved_as_new_versions()
    {
        await factory.CreateTenantAsync("gaps-pages");
        var client = await ApiClient.CreateAsync(factory, "gaps-pages");
        var (ws, list) = await LibraryAsync(client, "Pages");
        var upload = $"/v1.0/workspaces/{ws}/lists/{list}/documents";
        var a = (await UploadAsync(client, upload, Pages("Alpha page", "Beta page", "Gamma page"), "a.pdf")).GetProperty("itemId").GetGuid();
        var aUrl = Item(ws, list, a);
        await ProcessedAsync(client, aUrl);

        // DOC-05: delete page 2, move page 3 first, turn page 1.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{aUrl}/file/pages", new { pages = new[] { new { page = 4 } } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{aUrl}/file/pages", new { pages = Array.Empty<object>() }, Ct)).StatusCode);
        var before = (await (await client.GetAsync($"{aUrl}/file/versions", Ct)).ReadJsonAsync()).GetProperty("value").GetArrayLength();
        var edited = await client.PutAsJsonAsync($"{aUrl}/file/pages", new { pages = new object[] { new { page = 3 }, new { page = 1, rotate = 90 } } }, Ct);
        Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync(Ct));
        var version = await edited.ReadJsonAsync();
        Assert.Equal("pages", version.GetProperty("source").GetString());
        Assert.Equal(2, version.GetProperty("pageCount").GetInt32());
        Assert.Equal("succeeded", version.GetProperty("processingStatus").GetString());
        Assert.Equal([("Gamma page", 0), ("Alpha page", 90)], await DownloadPagesAsync(client, aUrl));
        var versions = (await (await client.GetAsync($"{aUrl}/file/versions", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal(before + 1, versions.Count); // Earlier versions stay.

        // Page texts were carried over: search finds the new version's pages without OCR.
        await Eventually.WaitForAsync<bool>(async () =>
        {
            var hits = (await (await client.GetAsync("/v1.0/search?q=Gamma", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray();
            return hits.Any(h => h.GetProperty("id").GetGuid() == a && h.TryGetProperty("page", out var page) && page.GetInt32() == 1) ? true : null;
        });

        // DOC-06 extract: page 2 into a new document, removed from the source.
        var extracted = await client.PostAsJsonAsync($"{aUrl}/file/pages/extract", new { pages = new[] { 2 }, remove = true, title = "Alpha only" }, Ct);
        Assert.True(extracted.StatusCode == HttpStatusCode.OK, await extracted.Content.ReadAsStringAsync(Ct));
        var result = await extracted.ReadJsonAsync();
        var document = Assert.Single(result.GetProperty("documents").EnumerateArray());
        Assert.Equal("Alpha only", document.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal(1, result.GetProperty("source").GetProperty("pageCount").GetInt32());
        var alphaUrl = Item(ws, list, document.GetProperty("itemId").GetGuid());
        Assert.Equal([("Alpha page", 90)], await DownloadPagesAsync(client, alphaUrl));
        Assert.Equal([("Gamma page", 0)], await DownloadPagesAsync(client, aUrl));

        // DOC-06 move (merge): every page of B into A; B goes to the recycle bin.
        var b = (await UploadAsync(client, upload, Pages("Delta page", "Epsilon page"), "b.pdf")).GetProperty("itemId").GetGuid();
        var bUrl = Item(ws, list, b);
        await ProcessedAsync(client, bUrl);
        var moved = await client.PostAsJsonAsync($"{bUrl}/file/pages/move",
            new { targetWorkspaceId = ws, targetListId = list, targetItemId = a, position = "prepend" }, Ct);
        Assert.True(moved.StatusCode == HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(Ct));
        var merge = await moved.ReadJsonAsync();
        Assert.True(merge.GetProperty("sourceDeleted").GetBoolean());
        Assert.Equal(3, merge.GetProperty("target").GetProperty("pageCount").GetInt32());
        Assert.Equal(["Delta page", "Epsilon page", "Gamma page"], (await DownloadPagesAsync(client, aUrl)).Select(p => p.Text));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(bUrl, Ct)).StatusCode);

        // Only PDFs that can be read are edited.
        var broken = (await UploadAsync(client, upload, "%PDF-1.4\n% broken\n%%EOF\n"u8.ToArray(), "broken.pdf")).GetProperty("itemId").GetGuid();
        var brokenResponse = await client.PutAsJsonAsync($"{Item(ws, list, broken)}/file/pages", new { pages = new[] { new { page = 1 } } }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, brokenResponse.StatusCode);
    }

    [Fact]
    public async Task Files_keep_their_own_languages()
    {
        await factory.CreateTenantAsync("gaps-languages");
        var client = await ApiClient.CreateAsync(factory, "gaps-languages");
        var (ws, list) = await LibraryAsync(client, "Languages");
        // Only the text layer is read (no OCR), so the test does not need Tesseract language data.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/documentSettings", new { ocrMode = "off" }, Ct)).StatusCode);
        var upload = $"/v1.0/workspaces/{ws}/lists/{list}/documents";

        var invalid = new MultipartFormDataContent { { new ByteArrayContent(Pages("Bonjour")), "file", "fr.pdf" }, { new StringContent("French!"), "languages" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(upload, invalid, Ct)).StatusCode);

        var created = await UploadAsync(client, upload, Pages("Bonjour tout le monde"), "fr.pdf", "fra");
        Assert.Equal("fra", created.GetProperty("file").GetProperty("languages").GetString());
        var url = Item(ws, list, created.GetProperty("itemId").GetGuid());
        var processed = Assert.Single(await ProcessedAsync(client, url));
        Assert.Equal("fra", processed.GetProperty("textLanguage").GetString());

        // A new upload keeps the file's languages; processing with other languages changes them.
        var replaced = await client.PutAsync($"{url}/file", new MultipartFormDataContent { { new ByteArrayContent(Pages("Au revoir")), "file", "fr2.pdf" } }, Ct);
        Assert.Equal("fra", (await replaced.ReadJsonAsync()).GetProperty("file").GetProperty("languages").GetString());
        await ProcessedAsync(client, url);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"{url}/file/process", new { languages = "deu" }, Ct)).StatusCode);
        var versions = await ProcessedAsync(client, url);
        Assert.Equal("deu", versions[0].GetProperty("languages").GetString());
        Assert.Equal("deu", versions[0].GetProperty("textLanguage").GetString());
    }

    [Fact]
    public async Task Group_members_upload_into_their_group_inbox()
    {
        await factory.CreateTenantAsync("gaps-inbox");
        var admin = await ApiClient.CreateAsync(factory, "gaps-inbox");
        var (ws, list) = await LibraryAsync(admin, "Accounting");
        async Task<Guid> UserAsync(string name)
        {
            var response = await admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
            var id = (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
            Assert.True((await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = id }, Ct)).IsSuccessStatusCode);
            return id;
        }

        var bob = await UserAsync("bob");
        await UserAsync("carol");
        var group = (await (await admin.PostAsJsonAsync("/v1.0/groups", new { name = "Accountants" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync($"/v1.0/groups/{group}/members", new { userId = bob }, Ct);
        var bobClient = await ApiClient.CreateAsync(factory, "gaps-inbox", "bob", "bob-password-1");
        var carolClient = await ApiClient.CreateAsync(factory, "gaps-inbox", "carol", "carol-password-1");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1.0/groups/{group}/inbox", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bobClient.PutAsJsonAsync($"/v1.0/groups/{group}/inbox", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/v1.0/groups/{Guid.NewGuid()}/inbox", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
        var set = await admin.PutAsJsonAsync($"/v1.0/groups/{group}/inbox", new { workspaceId = ws, listId = list }, Ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("Accountants", (await set.ReadJsonAsync()).GetProperty("groupName").GetString());

        var inboxes = (await (await bobClient.GetAsync("/v1.0/me/inboxes", Ct)).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal(["personal", "group"], inboxes.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(list, inboxes[1].GetProperty("listId").GetGuid());
        Assert.Single((await (await carolClient.GetAsync("/v1.0/me/inboxes", Ct)).ReadJsonAsync()).EnumerateArray());

        var uploaded = await UploadAsync(bobClient, $"/v1.0/groups/{group}/inbox/documents", Pages("Invoice 42"), "invoice.pdf");
        Assert.Equal(list, uploaded.GetProperty("listId").GetGuid());
        var form = new MultipartFormDataContent { { new ByteArrayContent(Pages("Nope")), "file", "nope.pdf" } };
        Assert.Equal(HttpStatusCode.NotFound, (await carolClient.PostAsync($"/v1.0/groups/{group}/inbox/documents", form, Ct)).StatusCode);

        // Portable: the library's settings name the group (PRV-05).
        var template = await (await admin.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("GroupInbox=\"Accountants\"", template, StringComparison.Ordinal);

        // The inbox goes with the group.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/groups/{group}", Ct)).StatusCode);
        await Eventually.WaitForAsync<bool>(async () =>
            (await admin.GetAsync($"/v1.0/groups/{group}/inbox", Ct)).StatusCode == HttpStatusCode.NotFound ? true : null);
    }

    [Fact]
    public async Task Group_inboxes_and_page_operations_are_isolated_per_tenant()
    {
        await factory.CreateTenantAsync("gaps-iso-a");
        await factory.CreateTenantAsync("gaps-iso-b");
        var a = await ApiClient.CreateAsync(factory, "gaps-iso-a");
        var b = await ApiClient.CreateAsync(factory, "gaps-iso-b");
        var (ws, list) = await LibraryAsync(a, "Private");
        var group = (await (await a.PostAsJsonAsync("/v1.0/groups", new { name = "Team" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsJsonAsync($"/v1.0/groups/{group}/inbox", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
        var item = (await UploadAsync(a, $"/v1.0/workspaces/{ws}/lists/{list}/documents", Pages("One", "Two"), "x.pdf")).GetProperty("itemId").GetGuid();
        var url = Item(ws, list, item);

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/v1.0/groups/{group}/inbox", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/v1.0/groups/{group}/inbox", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/v1.0/groups/{group}/inbox", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"{url}/file/pages", new { pages = new[] { new { page = 1 } } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"{url}/file/pages/extract", new { pages = new[] { 1 } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"{url}/file/pages/move",
            new { targetWorkspaceId = ws, targetListId = list, targetItemId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Single((await (await b.GetAsync("/v1.0/me/inboxes", Ct)).ReadJsonAsync()).EnumerateArray());
    }
}
