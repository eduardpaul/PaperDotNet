using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

public sealed class GlobalRelationPackageTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Packages_remap_relationships_and_repeated_imports_do_not_duplicate_them()
    {
        await factory.CreateTenantAsync("relations-package-src");
        var source = await ApiClient.CreateAsync(factory, "relations-package-src");
        var ws = await source.CreateWorkspaceAsync("First workspace");
        var otherWs = await source.CreateWorkspaceAsync("Second workspace");
        var type = await source.CreateContentTypeAsync("Record", []);
        var list = await source.CreateListAsync(ws, "Records", type);
        var otherList = await source.CreateListAsync(otherWs, "Records", type);
        async Task<Guid> CreateAsync(Guid workspace, Guid targetList, string title) =>
            (await source.CreateItemAsync(workspace, targetList, new { fields = new { title } })).GetProperty("id").GetGuid();
        var first = await CreateAsync(ws, list, "First record");
        var second = await CreateAsync(otherWs, otherList, "Second record");
        var third = await CreateAsync(ws, list, "Third record");
        (await source.PutAsync($"/v1.0/items/{first}/relations/{second}", null, Ct)).EnsureSuccessStatusCode();
        (await source.PutAsync($"/v1.0/items/{first}/relations/{third}", null, Ct)).EnsureSuccessStatusCode();

        // A workspace package includes its internal relationship, without references to outside workspaces.
        var workspaceExport = await source.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}&includeContent=true", Ct);
        workspaceExport.EnsureSuccessStatusCode();
        using (var zip = new ZipArchive(new MemoryStream(await workspaceExport.Content.ReadAsByteArrayAsync(Ct))))
        {
            var content = zip.Entries.Single(e => e.FullName.StartsWith("content/items-", StringComparison.Ordinal));
            using var document = await JsonDocument.ParseAsync(content.Open(), cancellationToken: Ct);
            var entry = document.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("title").GetString() == "First record");
            Assert.Equal(third.ToString("N"), Assert.Single(entry.GetProperty("relatedItems").EnumerateArray()).GetProperty("key").GetString());
        }

        // A tenant package carries the cross-workspace relationship too.
        var export = await source.GetAsync("/v1.0/provisioning/export?includeContent=true", Ct);
        export.EnsureSuccessStatusCode();
        var package = await export.Content.ReadAsByteArrayAsync(Ct);
        await factory.CreateTenantAsync("relations-package-dst");
        var target = await ApiClient.CreateAsync(factory, "relations-package-dst");
        for (var repeat = 0; repeat < 2; repeat++)
        {
            using var body = new ByteArrayContent(package);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            var applied = await target.PostAsync("/v1.0/provisioning/apply", body, Ct);
            Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync(Ct));
        }

        var items = (await (await target.GetAsync("/v1.0/items?q=record", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .ToDictionary(i => i.GetProperty("item").GetProperty("fields").GetProperty("title").GetString()!, i => i.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal(3, items.Count);
        Assert.NotEqual(first, items["First record"]);
        var links = await (await target.GetAsync($"/v1.0/items/{items["First record"]}/relations", Ct)).ReadJsonAsync();
        Assert.Equal(2, links.GetProperty("value").GetArrayLength());
        var inverse = await (await target.GetAsync($"/v1.0/items/{items["Second record"]}/relations", Ct)).ReadJsonAsync();
        Assert.Equal(items["First record"], Assert.Single(inverse.GetProperty("value").EnumerateArray()).GetProperty("item").GetProperty("id").GetGuid());
    }
}
