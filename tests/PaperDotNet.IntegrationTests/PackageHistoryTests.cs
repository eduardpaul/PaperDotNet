using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// Package additions of ADR-0029: every file version, page texts, original stamps and unique permissions travel in
/// packages and are kept on import.
/// </summary>
public sealed class PackageHistoryTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddText(text, 24, new PdfPoint(40, 760), builder.AddStandard14Font(Standard14Font.Helvetica));
        return builder.Build();
    }

    private static async Task<List<JsonElement>> VersionsAsync(HttpClient client, string itemUrl) =>
        [.. (await (await client.GetAsync($"{itemUrl}/file/versions", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()];

    [Fact]
    public async Task Versions_texts_stamps_and_permissions_travel_in_packages()
    {
        // Source: a folder with unique permissions for a group, a document with two processed versions.
        await factory.CreateTenantAsync("history-src");
        var source = await ApiClient.CreateAsync(factory, "history-src");
        await source.PostAsJsonAsync("/v1.0/users", new { userName = "dora", password = "dora-password-1" }, Ct);
        var team = (await (await source.PostAsJsonAsync("/v1.0/groups", new { name = "Team" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var ws = await source.CreateWorkspaceAsync("History");
        var library = (await (await source.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Files", templateKey = "documents" }, Ct)).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        var itemsUrl = $"/v1.0/workspaces/{ws}/lists/{library}/items";
        var folder = (await (await source.PostAsJsonAsync(itemsUrl, new { isFolder = true, fields = new { title = "Private" } }, Ct)).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await source.PostAsJsonAsync($"{itemsUrl}/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await source.PutAsJsonAsync($"{itemsUrl}/{folder}/permissions/grants",
            new { grants = new object[] { new { principalType = "group", principalId = team, level = "contribute" } } }, Ct)).StatusCode);
        var form = new MultipartFormDataContent { { new ByteArrayContent(Pdf("First draft walrus")), "file", "report.pdf" }, { new StringContent(folder.ToString()), "folderId" } };
        var uploaded = await source.PostAsync($"/v1.0/workspaces/{ws}/lists/{library}/documents", form, Ct);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var item = (await uploaded.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        var itemUrl = $"{itemsUrl}/{item}";
        await Eventually.WaitForAsync<bool>(async () => (await VersionsAsync(source, itemUrl)).All(v => v.GetProperty("processingStatus").GetString() == "succeeded") ? true : null);
        var replace = new MultipartFormDataContent { { new ByteArrayContent(Pdf("Final version narwhal")), "file", "report.pdf" } };
        Assert.Equal(HttpStatusCode.OK, (await source.PutAsync($"{itemUrl}/file", replace, Ct)).StatusCode);
        await Eventually.WaitForAsync<bool>(async () => (await VersionsAsync(source, itemUrl)).All(v => v.GetProperty("processingStatus").GetString() == "succeeded") ? true : null);
        var sourceItem = await (await source.GetAsync(itemUrl, Ct)).ReadJsonAsync();
        var sourceVersions = await VersionsAsync(source, itemUrl);

        var export = await source.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}&includeContent=true", Ct);
        var package = await export.Content.ReadAsByteArrayAsync(Ct);

        // Target: the same user and group names.
        await factory.CreateTenantAsync("history-dst");
        var target = await ApiClient.CreateAsync(factory, "history-dst");
        await target.PostAsJsonAsync("/v1.0/users", new { userName = "dora", password = "dora-password-1" }, Ct);
        await target.PostAsJsonAsync("/v1.0/groups", new { name = "Team" }, Ct);
        var content = new ByteArrayContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var applied = await target.PostAsync("/v1.0/provisioning/apply", content, Ct);
        Assert.True(applied.StatusCode == HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));

        var targetWs = (await (await target.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "History").GetProperty("id").GetGuid();
        var targetLibrary = (await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists", Ct)).ReadJsonAsync()).EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Files").GetProperty("id").GetGuid();
        var targetItems = (await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists/{targetLibrary}/items?$top=100", Ct)).ReadJsonAsync())
            .GetProperty("value").EnumerateArray().ToList();
        var targetFolder = targetItems.Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "Private");
        var targetItem = targetItems.Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "report");
        var targetUrl = $"/v1.0/workspaces/{targetWs}/lists/{targetLibrary}/items/{targetItem.GetProperty("id").GetGuid()}";

        // Original stamps.
        Assert.Equal(sourceItem.GetProperty("createdAt").GetDateTimeOffset(), targetItem.GetProperty("createdAt").GetDateTimeOffset());

        // Every version, with page texts (processed without running again).
        var versions = await VersionsAsync(target, targetUrl);
        Assert.Equal(sourceVersions.Count, versions.Count);
        Assert.All(versions, v => Assert.Equal("succeeded", v.GetProperty("processingStatus").GetString()));
        Assert.Equal(
            sourceVersions.Select(v => v.GetProperty("createdAt").GetDateTimeOffset()).Order(),
            versions.Select(v => v.GetProperty("createdAt").GetDateTimeOffset()).Order());
        await Eventually.WaitForAsync<bool>(async () =>
            (await (await target.GetAsync("/v1.0/search?q=narwhal", Ct)).ReadJsonAsync()).GetProperty("value").GetArrayLength() > 0 ? true : null);

        // Unique permissions of the folder, by group name.
        var permissions = await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists/{targetLibrary}/items/{targetFolder.GetProperty("id").GetGuid()}/permissions", Ct)).ReadJsonAsync();
        Assert.True(permissions.GetProperty("hasUniquePermissions").GetBoolean());
        Assert.Contains(permissions.GetProperty("grants").EnumerateArray(), g => g.GetProperty("principalType").GetString() == "group");
    }
}
