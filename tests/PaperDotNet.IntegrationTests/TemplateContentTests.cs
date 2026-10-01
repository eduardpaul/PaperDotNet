using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Templates with content (PRV-04): packages with items, folders, portable values, stamps and item permissions.</summary>
public sealed class TemplateContentTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<string> PostIdAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static async Task<List<JsonElement>> GetListAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        var body = await response.JsonAsync(HttpStatusCode.OK);
        return [.. (body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("value")).EnumerateArray()];
    }

    private static async Task<HttpResponseMessage> PostPackageAsync(HttpClient client, byte[] package, string query = "")
    {
        var content = new ByteArrayContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return await client.PostAsync($"/v1.0/provisioning/apply{query}", content, Ct);
    }

    private static async Task<JsonElement> ApplyAsync(HttpClient client, byte[] package, string query = "")
    {
        using var response = await PostPackageAsync(client, package, query);
        return await response.JsonAsync(HttpStatusCode.OK);
    }

    private static List<string> Changes(JsonElement result) =>
        [.. result.GetProperty("changes").EnumerateArray().Select(c => $"{c.GetProperty("kind").GetString()} {c.GetProperty("name").GetString()} {(c.TryGetProperty("detail", out var d) ? d.GetString() : null)}")];

    [Fact]
    public async Task Packages_carry_items_folders_values_and_permissions_to_another_tenant()
    {
        // Source: vendors, invoices in a folder with a lookup, a term, a person and keywords; one invoice with its own permissions.
        var alice = await PostIdAsync(_admin, "/v1.0/users", new { userName = "alice", password = "alice-password-1" });
        var termGroup = await PostIdAsync(_admin, "/v1.0/termStore/groups", new { name = "Finance" });
        var set = await PostIdAsync(_admin, "/v1.0/termStore/sets", new { groupId = termGroup, name = "Cost centers" });
        var sales = await PostIdAsync(_admin, $"/v1.0/termStore/sets/{set}/terms", new { name = "Sales" });
        var ws = await Api.CreateWorkspaceAsync(_admin, "Books");
        var vendorType = await Api.CreateContentTypeAsync(_admin, "Vendor", new[] { new { name = "city", displayName = "City", type = "text" } });
        var vendors = await PostIdAsync(_admin, $"/v1.0/workspaces/{ws}/lists", new { name = "Vendors", contentTypeIds = new[] { vendorType } });
        var acme = (await Api.CreateItemAsync(_admin, ws, vendors, new { title = "ACME", city = "Berlin" })).Id();
        var invoiceType = await Api.CreateContentTypeAsync(_admin, "Invoice", new object[]
        {
            new { name = "amount", displayName = "Amount", type = "number" },
            new { name = "vendor", displayName = "Vendor", type = "lookup", lookupListId = vendors },
            new { name = "costCenter", displayName = "Cost center", type = "managedMetadata", termSetId = set },
            new { name = "owner", displayName = "Owner", type = "person" },
            new { name = "tags", displayName = "Tags", type = "keywords", allowMultiple = true },
        });
        var invoices = await PostIdAsync(_admin, $"/v1.0/workspaces/{ws}/lists", new { name = "Invoices", contentTypeIds = new[] { invoiceType } });
        var folder = await PostIdAsync(_admin, Api.Items(ws, invoices), new { isFolder = true, fields = new { title = "2026" } });
        var invoice = await PostIdAsync(_admin, Api.Items(ws, invoices), new
        {
            parentId = folder,
            fields = new { title = "INV-1", amount = 120.5, vendor = acme, costCenter = sales, owner = alice, tags = new[] { "urgent" } },
        });
        var secret = await PostIdAsync(_admin, Api.Items(ws, invoices), new { fields = new { title = "INV-2", amount = 9 } });
        using (var broken = await _admin.PostAsJsonAsync($"{Api.Items(ws, invoices)}/{secret}/permissions/breakInheritance", new { copyGrants = false }, Ct))
        {
            await broken.JsonAsync(HttpStatusCode.OK);
        }

        using (var granted = await _admin.PutAsJsonAsync($"{Api.Items(ws, invoices)}/{secret}/permissions/grants",
            new { grants = new object[] { new { principalType = "user", principalId = alice, level = "read" } } }, Ct))
        {
            await granted.JsonAsync(HttpStatusCode.OK);
        }

        var sourceInvoice = await (await _admin.GetAsync($"{Api.Items(ws, invoices)}/{invoice}", Ct)).JsonAsync(HttpStatusCode.OK);

        // Export as a package.
        using var export = await _admin.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}&includeContent=true", Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        var package = await export.Content.ReadAsByteArrayAsync(Ct);
        using (var zip = new ZipArchive(new MemoryStream(package)))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("template.xml", names);
            Assert.Equal(2, names.Count(n => n.StartsWith("content/items-", StringComparison.Ordinal)));
            using var template = new StreamReader(zip.GetEntry("template.xml")!.Open());
            var xml = await template.ReadToEndAsync(Ct);
            Assert.Contains("<Items File=\"content/items-", xml, StringComparison.Ordinal);
            Assert.DoesNotContain(invoice, xml, StringComparison.OrdinalIgnoreCase);
        }

        // Target: a dry run plans the content; the apply creates it with the target's ids.
        var target = await _host.CreateTenantAsync("content-dst");
        var targetAlice = await PostIdAsync(target, "/v1.0/users", new { userName = "alice", password = "alice-password-1" });
        var plan = Changes(await ApplyAsync(target, package, "?dryRun=true"));
        Assert.Contains("items Books/Invoices 3 items", plan);
        Assert.Contains("items Books/Vendors 1 items", plan);
        Assert.Empty(await GetListAsync(target, "/v1.0/workspaces"));
        var applied = await ApplyAsync(target, package);
        Assert.Empty(applied.GetProperty("warnings").EnumerateArray());

        var targetWs = (await GetListAsync(target, "/v1.0/workspaces")).Single(w => w.GetProperty("name").GetString() == "Books").Id();
        var lists = await GetListAsync(target, $"/v1.0/workspaces/{targetWs}/lists");
        var targetVendors = lists.Single(l => l.GetProperty("name").GetString() == "Vendors").Id();
        var targetInvoices = lists.Single(l => l.GetProperty("name").GetString() == "Invoices").Id();
        var targetAcme = Assert.Single(await GetListAsync(target, Api.Items(targetWs, targetVendors)));
        Assert.Equal("Berlin", targetAcme.GetProperty("fields").GetProperty("city").GetString());
        Assert.NotEqual(acme, targetAcme.Id());
        var targetFolder = (await GetListAsync(target, Api.Items(targetWs, targetInvoices))).Single(i => i.GetProperty("isFolder").GetBoolean());
        var copy = Assert.Single(await GetListAsync(target, $"{Api.Items(targetWs, targetInvoices)}/{targetFolder.Id()}/children"));
        var fields = copy.GetProperty("fields");
        Assert.Equal("INV-1", fields.GetProperty("title").GetString());
        Assert.Equal(120.5m, fields.GetProperty("amount").GetDecimal());
        Assert.Equal(targetAcme.Id(), fields.GetProperty("vendor").GetString());
        Assert.Equal(targetAlice, fields.GetProperty("owner").GetString());
        Assert.NotEqual(sales, fields.GetProperty("costCenter").GetString());
        var targetSet = (await GetListAsync(target, "/v1.0/termStore/sets")).Single(t => t.GetProperty("name").GetString() == "Cost centers").Id();
        var term = await (await target.GetAsync($"/v1.0/termStore/sets/{targetSet}/terms/{fields.GetProperty("costCenter").GetString()}", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Sales", term.GetProperty("name").GetString());
        Assert.Equal(1, fields.GetProperty("tags").GetArrayLength());

        // Created and changed stamps are kept (the source admin has no account on the target).
        Assert.Equal(sourceInvoice.GetProperty("createdAt").GetDateTimeOffset(), copy.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(sourceInvoice.GetProperty("updatedAt").GetDateTimeOffset(), copy.GetProperty("updatedAt").GetDateTimeOffset());

        // The invoice with its own permissions keeps them: alice can read it.
        var targetSecret = (await GetListAsync(target, Api.Items(targetWs, targetInvoices))).Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "INV-2");
        var permissions = await (await target.GetAsync($"{Api.Items(targetWs, targetInvoices)}/{targetSecret.Id()}/permissions", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.True(permissions.GetProperty("hasUniquePermissions").GetBoolean());
        Assert.Contains(permissions.GetProperty("grants").EnumerateArray(),
            g => g.GetProperty("principalType").GetString() == "user" && g.GetProperty("principalId").GetString() == targetAlice && g.GetProperty("level").GetString() == "read");

        // Applying again changes nothing.
        Assert.Empty(Changes(await ApplyAsync(target, package)));
        Assert.Single(await GetListAsync(target, Api.Items(targetWs, targetVendors)));
    }

    [Fact]
    public async Task Content_sections_need_a_package_and_packages_are_checked()
    {
        const string template = """
            <Template xmlns="urn:paperdotnet:template:1" SchemaVersion="1.0" Scope="Workspace">
              <Workspaces><Workspace Name="W"><Lists><List Name="L"><Items File="content/items.json" /></List></Lists></Workspace></Workspaces>
            </Template>
            """;
        using var xml = await _admin.PostAsync("/v1.0/provisioning/apply", new StringContent(template, Encoding.UTF8, "application/xml"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, xml.StatusCode);
        Assert.Contains("needs the template package", await xml.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var notZip = await PostPackageAsync(_admin, "not a zip"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, notZip.StatusCode);

        // A package whose items document is missing.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = await zip.CreateEntry("template.xml").OpenAsync(Ct);
            await entry.WriteAsync(Encoding.UTF8.GetBytes(template), Ct);
        }

        using var response = await PostPackageAsync(_admin, buffer.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("has no content/items.json", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(await GetListAsync(_admin, "/v1.0/workspaces"), w => w.GetProperty("name").GetString() == "W");
    }
}
