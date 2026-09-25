using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Templates with content (PRV-04): packages with items, folders, portable values and files.</summary>
public sealed class TemplateContentTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient client, Guid ws, Guid list) =>
        [.. (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items?$top=100", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()];

    private static async Task<Guid> ListIdAsync(HttpClient client, Guid ws, string name) =>
        (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists", Ct)).ReadJsonAsync()).EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    private static async Task<JsonElement> ApplyAsync(HttpClient client, byte[] package, string query = "")
    {
        var content = new ByteArrayContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var response = await client.PostAsync($"/v1.0/provisioning/apply{query}", content, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static List<string> Changes(JsonElement result) =>
        [.. result.GetProperty("changes").EnumerateArray().Select(c => $"{c.GetProperty("kind").GetString()} {c.GetProperty("name").GetString()} {(c.TryGetProperty("detail", out var d) ? d.GetString() : null)}")];

    [Fact]
    public async Task Packages_carry_items_folders_values_and_files_to_another_tenant()
    {
        // Source: vendors, invoices in a folder with a lookup, a term, a person and keywords, and a library with a PDF.
        await factory.CreateTenantAsync("content-src");
        var source = await ApiClient.CreateAsync(factory, "content-src");
        var alice = await PostIdAsync(source, "/v1.0/users", new { userName = "alice", password = "alice-password-1" });
        var termGroup = await PostIdAsync(source, "/v1.0/termStore/groups", new { name = "Finance" });
        var set = await PostIdAsync(source, "/v1.0/termStore/sets", new { groupId = termGroup, name = "Cost centers" });
        var sales = await PostIdAsync(source, $"/v1.0/termStore/sets/{set}/terms", new { name = "Sales" });
        var ws = await source.CreateWorkspaceAsync("Books");
        var vendorType = await source.CreateContentTypeAsync("Vendor", [new { name = "city", type = "text" }]);
        var vendors = await source.CreateListAsync(ws, "Vendors", vendorType);
        var acme = (await source.CreateItemAsync(ws, vendors, new { fields = new { title = "ACME", city = "Berlin" } })).GetProperty("id").GetGuid();
        var invoiceType = await source.CreateContentTypeAsync("Invoice", [
            new { name = "amount", type = "number" },
            new { name = "vendor", type = "lookup", lookupListId = vendors },
            new { name = "costCenter", type = "managedMetadata", termSetId = set },
            new { name = "owner", type = "person" },
            new { name = "tags", type = "keywords", allowMultiple = true },
        ]);
        var invoices = await source.CreateListAsync(ws, "Invoices", invoiceType);
        var folder = (await source.CreateItemAsync(ws, invoices, new { isFolder = true, fields = new { title = "2026" } })).GetProperty("id").GetGuid();
        await source.CreateItemAsync(ws, invoices, new
        {
            parentId = folder,
            fields = new { title = "INV-1", amount = 120.5, vendor = acme, costCenter = sales, owner = alice, tags = new[] { "urgent" } },
        });
        var library = await PostIdAsync(source, $"/v1.0/workspaces/{ws}/lists", new { name = "Scans", templateKey = "documents" });
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% sample scan\n%%EOF\n");
        var upload = await source.PostAsync($"/v1.0/workspaces/{ws}/lists/{library}/documents",
            new MultipartFormDataContent { { new ByteArrayContent(pdf), "file", "scan.pdf" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        // Export as a package.
        var export = await source.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}&includeContent=true", Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        var package = await export.Content.ReadAsByteArrayAsync(Ct);
        using (var zip = new ZipArchive(new MemoryStream(package)))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("template.xml", names);
            Assert.Contains(names, n => n.StartsWith("content/items-", StringComparison.Ordinal));
            Assert.Single(names, n => n.StartsWith("files/", StringComparison.Ordinal));
            using var template = new StreamReader(zip.GetEntry("template.xml")!.Open());
            Assert.Contains("<Items File=\"content/items-", await template.ReadToEndAsync(Ct), StringComparison.Ordinal);
        }

        // Target: a dry run plans the content; the apply creates it with the target's ids.
        await factory.CreateTenantAsync("content-dst");
        var target = await ApiClient.CreateAsync(factory, "content-dst");
        var targetAlice = await PostIdAsync(target, "/v1.0/users", new { userName = "alice", password = "alice-password-1" });
        var plan = Changes(await ApplyAsync(target, package, "?dryRun=true"));
        Assert.Contains("items Books/Invoices 2 items", plan);
        Assert.Contains("files Books/Scans 1 files", plan);
        var applied = await ApplyAsync(target, package);
        Assert.Empty(applied.GetProperty("warnings").EnumerateArray());

        var targetWs = (await (await target.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "Books").GetProperty("id").GetGuid();
        var targetVendors = await ListIdAsync(target, targetWs, "Vendors");
        var targetAcme = (await ItemsAsync(target, targetWs, targetVendors)).Single();
        Assert.Equal("Berlin", targetAcme.GetProperty("fields").GetProperty("city").GetString());
        var targetInvoices = await ListIdAsync(target, targetWs, "Invoices");
        var targetFolder = (await ItemsAsync(target, targetWs, targetInvoices)).Single(i => i.TryGetProperty("isFolder", out var f) && f.GetBoolean());
        var invoice = (await (await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists/{targetInvoices}/items/{targetFolder.GetProperty("id").GetGuid()}/children", Ct))
            .ReadJsonAsync()).GetProperty("value").EnumerateArray().Single();
        var fields = invoice.GetProperty("fields");
        Assert.Equal("INV-1", fields.GetProperty("title").GetString());
        Assert.Equal(120.5m, fields.GetProperty("amount").GetDecimal());
        Assert.Equal(targetAcme.GetProperty("id").GetGuid(), fields.GetProperty("vendor").GetGuid());
        Assert.Equal(targetAlice, fields.GetProperty("owner").GetGuid());
        Assert.NotEqual(sales, fields.GetProperty("costCenter").GetGuid());
        var targetSet = (await (await target.GetAsync("/v1.0/termStore/sets", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "Cost centers").GetProperty("id").GetGuid();
        var term = await (await target.GetAsync($"/v1.0/termStore/sets/{targetSet}/terms/{fields.GetProperty("costCenter").GetGuid()}", Ct)).ReadJsonAsync();
        Assert.Equal("Sales", term.GetProperty("name").GetString());
        Assert.Equal(1, fields.GetProperty("tags").GetArrayLength());

        var targetScans = await ListIdAsync(target, targetWs, "Scans");
        var scan = (await ItemsAsync(target, targetWs, targetScans)).Single();
        var download = await target.GetAsync($"/v1.0/workspaces/{targetWs}/lists/{targetScans}/items/{scan.GetProperty("id").GetGuid()}/file", Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(pdf, await download.Content.ReadAsByteArrayAsync(Ct));

        // Applying again changes nothing.
        Assert.Empty(Changes(await ApplyAsync(target, package)));
        Assert.Single(await ItemsAsync(target, targetWs, targetScans));
    }

    [Fact]
    public async Task Content_sections_need_a_package_and_packages_are_checked()
    {
        await factory.CreateTenantAsync("content-checks");
        var client = await ApiClient.CreateAsync(factory, "content-checks");
        const string Template = """
            <Template xmlns="urn:paperdotnet:template:1" SchemaVersion="1.0" Scope="Workspace">
              <Workspaces><Workspace Name="W"><Lists><List Name="L"><Items File="content/items.json" /></List></Lists></Workspace></Workspaces>
            </Template>
            """;
        var xml = await client.PostAsync("/v1.0/provisioning/apply", new StringContent(Template, Encoding.UTF8, "application/xml"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, xml.StatusCode);
        Assert.Contains("needs the template package", await xml.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var notZip = new ByteArrayContent("not a zip"u8.ToArray());
        notZip.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/v1.0/provisioning/apply", notZip, Ct)).StatusCode);

        // A package whose items document is missing.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = await zip.CreateEntry("template.xml").OpenAsync(Ct);
            await entry.WriteAsync(Encoding.UTF8.GetBytes(Template), Ct);
        }

        var missing = new ByteArrayContent(buffer.ToArray());
        missing.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var response = await client.PostAsync("/v1.0/provisioning/apply", missing, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("has no content/items.json", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}
