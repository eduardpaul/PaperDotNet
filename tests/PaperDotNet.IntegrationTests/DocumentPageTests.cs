using System.Net;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// Documents (T15c): page operations (DOC-05/06), languages per file (DOC-17), the Home inbox (LST-07), group inboxes
/// (DOC-16) and their template section (PRV-05), fields on folders (LST-19).
/// </summary>
public sealed class DocumentPageTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

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

    private static async Task<(string Workspace, string Library)> LibraryAsync(HttpClient client, string name)
    {
        var ws = await Api.CreateWorkspaceAsync(client, name);
        using var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        return (ws, (await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private static async Task<JsonElement> UploadAsync(HttpClient client, string url, byte[] content, string fileName, string? languages = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        if (languages is not null)
        {
            form.Add(new StringContent(languages), "languages");
        }

        using var response = await client.PostAsync(url, form, Ct);
        return await response.JsonAsync(HttpStatusCode.Created);
    }

    private static string Item(string ws, string list, string item) => $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}";

    /// <summary>Waits until the item has <paramref name="runs"/> ended workflow runs (three per version: text, thumbnail, pages); returns its versions.</summary>
    private static async Task<List<JsonElement>> ProcessedAsync(HttpClient client, string ws, string itemUrl, string item, int runs = 3)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var found = (await (await client.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?itemId={item}", Ct)).JsonAsync(HttpStatusCode.OK))
                .GetProperty("value").EnumerateArray().ToList();
            if (found.Count >= runs && found.All(r => r.GetProperty("status").GetString() is "completed" or "failed"))
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, $"{found.Count} runs");
            await Task.Delay(100, Ct);
        }

        return [.. (await (await client.GetAsync($"{itemUrl}/file/versions", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()];
    }

    private static async Task<List<(string Text, int Rotation)>> DownloadPagesAsync(HttpClient client, string itemUrl)
    {
        var bytes = await client.GetByteArrayAsync($"{itemUrl}/file", Ct);
        using var pdf = PdfDocument.Open(bytes);
        return [.. pdf.GetPages().Select(p => (p.Text.Trim(), p.Rotation.Value))];
    }

    [Fact]
    public async Task Folders_hold_optional_field_values()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Folders");
        var list = (await Api.CreateListAsync(_admin, ws, "Cases", new object[]
        {
            new { name = "tags", displayName = "Tags", type = "keywords", allowMultiple = true },
            new { name = "amount", displayName = "Amount", type = "number", required = true },
        })).Id();
        var url = Api.Items(ws, list);

        using var folder = await _admin.PostAsJsonAsync(url, new { isFolder = true, fields = new { title = "2026", tags = new[] { "archive" } } }, Ct);
        var created = await folder.JsonAsync(HttpStatusCode.Created);
        Assert.Equal(1, created.GetProperty("fields").GetProperty("tags").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync(url, new { isFolder = true, fields = new { title = "Bad", amount = "many" } }, Ct)).StatusCode);

        // Items still need their required fields.
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync(url, new { fields = new { title = "No amount" } }, Ct)).StatusCode);

        using var updated = await _admin.SendAsync(Api.Patch($"{url}/{created.Id()}", new { fields = new { tags = new[] { "archive", "2026" } } }, created.ETag()), Ct);
        Assert.Equal(2, (await updated.JsonAsync(HttpStatusCode.OK)).GetProperty("fields").GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public async Task Pages_are_edited_extracted_and_moved_as_new_versions()
    {
        var (ws, list) = await LibraryAsync(_admin, "Pages");
        var upload = $"/v1.0/workspaces/{ws}/lists/{list}/documents";
        var a = (await UploadAsync(_admin, upload, Pages("Alpha page", "Beta page", "Gamma page"), "a.pdf")).GetProperty("itemId").GetString()!;
        var aUrl = Item(ws, list, a);
        var before = (await ProcessedAsync(_admin, ws, aUrl, a)).Count;

        // DOC-05: delete page 2, move page 3 first, turn page 1.
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync($"{aUrl}/file/pages", new { pages = new[] { new { page = 4 } } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync($"{aUrl}/file/pages", new { pages = Array.Empty<object>() }, Ct)).StatusCode);
        using (var edited = await _admin.PutAsJsonAsync($"{aUrl}/file/pages", new { pages = new object[] { new { page = 3 }, new { page = 1, rotate = 90 } } }, Ct))
        {
            var version = await edited.JsonAsync(HttpStatusCode.OK);
            Assert.Equal("pages", version.GetProperty("source").GetString());
            Assert.Equal(2, version.GetProperty("pageCount").GetInt32());
        }

        Assert.Equal([("Gamma page", 0), ("Alpha page", 90)], await DownloadPagesAsync(_admin, aUrl));
        var versions = (await (await _admin.GetAsync($"{aUrl}/file/versions", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").GetArrayLength();
        Assert.Equal(before + 1, versions); // Earlier versions stay.

        // Page texts were carried over: search finds the new version's pages without reading them again.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await (await _admin.GetAsync("/v1.0/search?q=Gamma", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
            .Any(h => h.GetProperty("id").GetString() == a && h.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Number && page.GetInt32() == 1))
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }

        // DOC-06 extract: page 2 into a new document, removed from the source.
        using (var extracted = await _admin.PostAsJsonAsync($"{aUrl}/file/pages/extract", new { pages = new[] { 2 }, remove = true, title = "Alpha only" }, Ct))
        {
            var result = await extracted.JsonAsync(HttpStatusCode.OK);
            var document = Assert.Single(result.GetProperty("documents").EnumerateArray());
            Assert.Equal("Alpha only", document.GetProperty("fields").GetProperty("title").GetString());
            Assert.Equal(1, result.GetProperty("source").GetProperty("pageCount").GetInt32());
            Assert.Equal([("Alpha page", 90)], await DownloadPagesAsync(_admin, Item(ws, list, document.GetProperty("itemId").GetString()!)));
        }

        Assert.Equal([("Gamma page", 0)], await DownloadPagesAsync(_admin, aUrl));

        // DOC-06 move (merge): every page of B into A; B goes to the recycle bin.
        var b = (await UploadAsync(_admin, upload, Pages("Delta page", "Epsilon page"), "b.pdf")).GetProperty("itemId").GetString()!;
        var bUrl = Item(ws, list, b);
        await ProcessedAsync(_admin, ws, bUrl, b);
        using (var moved = await _admin.PostAsJsonAsync($"{bUrl}/file/pages/move", new { targetWorkspaceId = ws, targetListId = list, targetItemId = a, position = "prepend" }, Ct))
        {
            var merge = await moved.JsonAsync(HttpStatusCode.OK);
            Assert.True(merge.GetProperty("sourceDeleted").GetBoolean());
            Assert.Equal(3, merge.GetProperty("target").GetProperty("pageCount").GetInt32());
        }

        Assert.Equal(["Delta page", "Epsilon page", "Gamma page"], (await DownloadPagesAsync(_admin, aUrl)).Select(p => p.Text));
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(bUrl, Ct)).StatusCode);

        // Only PDFs that can be read are edited.
        var broken = (await UploadAsync(_admin, upload, "%PDF-1.4\n% broken\n%%EOF\n"u8.ToArray(), "broken.pdf")).GetProperty("itemId").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PutAsJsonAsync($"{Item(ws, list, broken)}/file/pages", new { pages = new[] { new { page = 1 } } }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Files_keep_their_own_languages()
    {
        var (ws, list) = await LibraryAsync(_admin, "Languages");
        var upload = $"/v1.0/workspaces/{ws}/lists/{list}/documents";
        var invalid = new MultipartFormDataContent { { new ByteArrayContent(Pages("Bonjour")), "file", "fr.pdf" }, { new StringContent("French!"), "languages" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsync(upload, invalid, Ct)).StatusCode);

        var created = await UploadAsync(_admin, upload, Pages("Bonjour tout le monde"), "fr.pdf", "fra");
        Assert.Equal("fra", created.GetProperty("file").GetProperty("languages").GetString());
        var item = created.GetProperty("itemId").GetString()!;
        var url = Item(ws, list, item);
        Assert.Equal("fra", Assert.Single(await ProcessedAsync(_admin, ws, url, item)).GetProperty("textLanguage").GetString());

        // A new upload keeps the file's languages, and its text is read in them.
        using var replaced = await _admin.PutAsync($"{url}/file", new MultipartFormDataContent { { new ByteArrayContent(Pages("Au revoir")), "file", "fr2.pdf" } }, Ct);
        Assert.Equal("fra", (await replaced.JsonAsync(HttpStatusCode.OK)).GetProperty("file").GetProperty("languages").GetString());
        var versions = await ProcessedAsync(_admin, ws, url, item, runs: 6);
        Assert.Equal("fra", versions[0].GetProperty("languages").GetString());
        Assert.Equal("fra", versions[0].GetProperty("textLanguage").GetString());
    }

    [Fact]
    public async Task Uploads_to_the_inbox_land_in_the_home_workspace()
    {
        var home = await (await _admin.GetAsync("/v1.0/me/home", Ct)).JsonAsync(HttpStatusCode.OK);
        var form = new MultipartFormDataContent { { new ByteArrayContent(Pages("Copier")), "file", "scan.pdf" }, { new StringContent("Scan from the copier"), "title" } };
        using var response = await _admin.PostAsync("/v1.0/me/inbox/documents", form, Ct);
        var document = await response.JsonAsync(HttpStatusCode.Created);
        Assert.Equal(home.GetProperty("inboxListId").GetString(), document.GetProperty("listId").GetString());
        Assert.Equal(home.GetProperty("workspaceId").GetString(), document.GetProperty("workspaceId").GetString());
        Assert.Equal("Scan from the copier", document.GetProperty("fields").GetProperty("title").GetString());

        // The Home libraries are libraries of documents, and the same on every call.
        var ws = home.GetProperty("workspaceId").GetString();
        var library = home.GetProperty("documentsListId").GetString();
        var list = await (await _admin.GetAsync($"/v1.0/workspaces/{ws}/lists/{library}", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("document", list.GetProperty("contentTypes")[0].GetProperty("key").GetString());
        Assert.Equal(library, (await (await _admin.GetAsync("/v1.0/me/home", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("documentsListId").GetString());
        var inboxes = (await (await _admin.GetAsync("/v1.0/me/inboxes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal("personal", Assert.Single(inboxes).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Group_members_upload_into_their_group_inbox()
    {
        var (ws, list) = await LibraryAsync(_admin, "Accounting");
        async Task<string> UserAsync(string name)
        {
            using var response = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
            var id = (await response.JsonAsync(HttpStatusCode.Created)).Id();
            using var member = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = id, role = "member" }, Ct);
            Assert.True(member.IsSuccessStatusCode);
            return id;
        }

        var bob = await UserAsync("bob");
        await UserAsync("carol");
        string group;
        using (var created = await _admin.PostAsJsonAsync("/v1.0/groups", new { name = "Accountants" }, Ct))
        {
            group = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        }

        Assert.True((await _admin.PostAsJsonAsync($"/v1.0/groups/{group}/members", new { userId = bob }, Ct)).IsSuccessStatusCode);
        var bobClient = await _host.SignInAsync("bob", "bob-password-1");
        var carolClient = await _host.SignInAsync("carol", "carol-password-1");

        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/v1.0/groups/{group}/inbox", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bobClient.PutAsJsonAsync($"/v1.0/groups/{group}/inbox", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PutAsJsonAsync($"/v1.0/groups/{Guid.NewGuid()}/inbox", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
        using (var set = await _admin.PutAsJsonAsync($"/v1.0/groups/{group}/inbox", new { workspaceId = ws, listId = list }, Ct))
        {
            Assert.Equal("Accountants", (await set.JsonAsync(HttpStatusCode.OK)).GetProperty("groupName").GetString());
        }

        var inboxes = (await (await bobClient.GetAsync("/v1.0/me/inboxes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal(["personal", "group"], inboxes.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(list, inboxes[1].GetProperty("listId").GetString());
        Assert.Single((await (await carolClient.GetAsync("/v1.0/me/inboxes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());

        var uploaded = await UploadAsync(bobClient, $"/v1.0/groups/{group}/inbox/documents", Pages("Invoice 42"), "invoice.pdf");
        Assert.Equal(list, uploaded.GetProperty("listId").GetString());
        var form = new MultipartFormDataContent { { new ByteArrayContent(Pages("Nope")), "file", "nope.pdf" } };
        Assert.Equal(HttpStatusCode.NotFound, (await carolClient.PostAsync($"/v1.0/groups/{group}/inbox/documents", form, Ct)).StatusCode);

        // Portable: the library's settings name the group (PRV-05).
        var template = await (await _admin.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("GroupInbox=\"Accountants\"", template, StringComparison.Ordinal);

        // The inbox goes with the group.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/groups/{group}", Ct)).StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await _admin.GetAsync($"/v1.0/groups/{group}/inbox", Ct)).StatusCode != HttpStatusCode.NotFound)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Versions_texts_stamps_and_permissions_travel_in_packages()
    {
        // Source: a folder with unique permissions for a group, a document with two processed versions.
        var source = await _host.CreateTenantAsync("history-source");
        Assert.True((await source.PostAsJsonAsync("/v1.0/users", new { userName = "dora", password = "dora-password-1" }, Ct)).IsSuccessStatusCode);
        string team;
        using (var created = await source.PostAsJsonAsync("/v1.0/groups", new { name = "Team" }, Ct))
        {
            team = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        }

        var (ws, library) = await LibraryAsync(source, "History");
        var itemsUrl = Api.Items(ws, library);
        var folder = (await (await source.PostAsJsonAsync(itemsUrl, new { isFolder = true, fields = new { title = "Private" } }, Ct)).JsonAsync(HttpStatusCode.Created)).Id();
        Assert.Equal(HttpStatusCode.OK, (await source.PostAsJsonAsync($"{itemsUrl}/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await source.PutAsJsonAsync($"{itemsUrl}/{folder}/permissions/grants",
            new { grants = new object[] { new { principalType = "group", principalId = team, level = "contribute" } } }, Ct)).StatusCode);
        var form = new MultipartFormDataContent { { new ByteArrayContent(Pages("First draft walrus")), "file", "report.pdf" }, { new StringContent(folder), "folderId" } };
        string item;
        using (var uploaded = await source.PostAsync($"/v1.0/workspaces/{ws}/lists/{library}/documents", form, Ct))
        {
            item = (await uploaded.JsonAsync(HttpStatusCode.Created)).GetProperty("itemId").GetString()!;
        }

        var itemUrl = $"{itemsUrl}/{item}";
        await ProcessedAsync(source, ws, itemUrl, item);
        using (var replaced = await source.PutAsync($"{itemUrl}/file", new MultipartFormDataContent { { new ByteArrayContent(Pages("Final version narwhal")), "file", "report.pdf" } }, Ct))
        {
            await replaced.JsonAsync(HttpStatusCode.OK);
        }

        var sourceVersions = await ProcessedAsync(source, ws, itemUrl, item, runs: 6);
        var sourceItem = await (await source.GetAsync(itemUrl, Ct)).JsonAsync(HttpStatusCode.OK);
        var package = await (await source.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}&includeContent=true", Ct)).Content.ReadAsByteArrayAsync(Ct);

        // Target: the same user and group names.
        var target = await _host.CreateTenantAsync("history-target");
        Assert.True((await target.PostAsJsonAsync("/v1.0/users", new { userName = "dora", password = "dora-password-1" }, Ct)).IsSuccessStatusCode);
        Assert.True((await target.PostAsJsonAsync("/v1.0/groups", new { name = "Team" }, Ct)).IsSuccessStatusCode);
        var content = new ByteArrayContent(package);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        using (var applied = await target.PostAsync("/v1.0/provisioning/apply", content, Ct))
        {
            await applied.JsonAsync(HttpStatusCode.OK);
        }

        var targetWs = (await (await target.GetAsync("/v1.0/workspaces", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "History").Id();
        var targetLibrary = (await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Documents").Id();
        var targetItems = (await (await target.GetAsync($"{Api.Items(targetWs, targetLibrary)}?$top=100", Ct)).JsonAsync(HttpStatusCode.OK))
            .GetProperty("value").EnumerateArray().ToList();
        var targetFolder = targetItems.Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "Private").Id();
        var targetItem = targetItems.Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "report");
        var targetUrl = $"{Api.Items(targetWs, targetLibrary)}/{targetItem.Id()}";

        // Original stamps; every version with its page texts (read from the package, not again).
        Assert.Equal(sourceItem.GetProperty("createdAt").GetDateTimeOffset(), targetItem.GetProperty("createdAt").GetDateTimeOffset());
        var versions = (await (await target.GetAsync($"{targetUrl}/file/versions", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal(sourceVersions.Count, versions.Count);
        Assert.All(versions, v => Assert.Equal(1, v.GetProperty("pageCount").GetInt32()));
        Assert.Equal(
            sourceVersions.Select(v => v.GetProperty("createdAt").GetDateTimeOffset()).Order(),
            versions.Select(v => v.GetProperty("createdAt").GetDateTimeOffset()).Order());
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await (await target.GetAsync("/v1.0/search?q=narwhal", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").GetArrayLength() == 0)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }

        // Unique permissions of the folder, by group name.
        var permissions = await (await target.GetAsync($"{Api.Items(targetWs, targetLibrary)}/{targetFolder}/permissions", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.True(permissions.GetProperty("hasUniquePermissions").GetBoolean());
        Assert.Contains(permissions.GetProperty("grants").EnumerateArray(), g => g.GetProperty("principalType").GetString() == "group");
    }

    [Fact]
    public async Task Group_inboxes_and_page_operations_are_isolated_per_tenant()
    {
        var (ws, list) = await LibraryAsync(_admin, "Private");
        string group;
        using (var created = await _admin.PostAsJsonAsync("/v1.0/groups", new { name = "Team" }, Ct))
        {
            group = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        }

        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"/v1.0/groups/{group}/inbox", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
        var item = (await UploadAsync(_admin, $"/v1.0/workspaces/{ws}/lists/{list}/documents", Pages("One", "Two"), "x.pdf")).GetProperty("itemId").GetString()!;
        var url = Item(ws, list, item);

        var other = await _host.CreateTenantAsync("pages-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/groups/{group}/inbox", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/v1.0/groups/{group}/inbox", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/groups/{group}/inbox", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{url}/file/pages", new { pages = new[] { new { page = 1 } } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{url}/file/pages/extract", new { pages = new[] { 1 } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{url}/file/pages/move",
            new { targetWorkspaceId = ws, targetListId = list, targetItemId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Single((await (await other.GetAsync("/v1.0/me/inboxes", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray());
    }
}
