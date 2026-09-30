using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace PaperDotNet.IntegrationTests;

/// <summary>Provisioning templates (phase 5a, PRV-01…03, PRV-05): export, apply, dry run, parameters, schema.</summary>
public sealed class ProvisioningTests(PaperDotNetApiFactory factory)
{
    private static readonly XNamespace T = "urn:paperdotnet:template:1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> ExportAsync(HttpClient client, Guid? workspaceId = null)
    {
        var response = await client.GetAsync($"/v1.0/provisioning/export{(workspaceId is null ? "" : $"?workspaceId={workspaceId}")}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, string template, string query = "") =>
        client.PostAsync($"/v1.0/provisioning/apply{query}", new StringContent(template, Encoding.UTF8, "application/xml"), Ct);

    private static async Task<JsonElement> ApplyOkAsync(HttpClient client, string template, string query = "")
    {
        var response = await ApplyAsync(client, template, query);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static List<string> Changes(JsonElement result) =>
        result.GetProperty("changes").EnumerateArray()
            .Select(c => $"{c.GetProperty("action").GetString()} {c.GetProperty("kind").GetString()} {c.GetProperty("name").GetString()}").ToList();

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> GetListAsync(HttpClient client, string url)
    {
        var body = await (await client.GetAsync(url, Ct)).ReadJsonAsync();
        return (body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("value")).EnumerateArray().ToList();
    }

    /// <summary>A workspace with a lookup between lists, managed metadata, a view, unique permissions and library settings.</summary>
    private static async Task<Guid> BuildFinanceAsync(HttpClient admin)
    {
        var alice = await PostIdAsync(admin, "/v1.0/users", new { userName = "alice", password = "alice-password-1" });
        var group = await PostIdAsync(admin, "/v1.0/groups", new { name = "Accountants", description = "Books and invoices" });
        await admin.PostAsJsonAsync($"/v1.0/groups/{group}/members", new { userId = alice }, Ct);

        var termGroup = await PostIdAsync(admin, "/v1.0/termStore/groups", new { name = "Finance" });
        var set = await PostIdAsync(admin, "/v1.0/termStore/sets", new { groupId = termGroup, name = "Cost centers" });
        var sales = await PostIdAsync(admin, $"/v1.0/termStore/sets/{set}/terms", new { name = "Sales", color = "#1f77b4", labels = new[] { new { language = "de", name = "Vertrieb" } } });
        await PostIdAsync(admin, $"/v1.0/termStore/sets/{set}/terms", new { name = "Field sales", parentId = sales, synonyms = new[] { "Outside sales" } });

        var ws = await admin.CreateWorkspaceAsync("Finance");
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = alice, role = "member" }, Ct);
        var vendor = await admin.CreateContentTypeAsync("Vendor", [new { name = "country", type = "choice", choices = new[] { "DE", "FR" } }]);
        var vendors = await admin.CreateListAsync(ws, "Vendors", vendor);
        var invoice = await admin.CreateContentTypeAsync("Invoice", [
            new { name = "amount", displayName = "Amount", type = "currency", currencyCode = "EUR", required = true },
            new { name = "costCenter", type = "managedMetadata", termSetId = set },
            new { name = "vendor", type = "lookup", lookupListId = vendors },
        ]);
        var invoices = await PostIdAsync(admin, $"/v1.0/workspaces/{ws}/lists", new { name = "Invoices", kind = "library", contentTypeIds = new[] { invoice } });
        await PostIdAsync(admin, $"/v1.0/workspaces/{ws}/lists/{invoices}/views",
            new { name = "By cost center", columns = new[] { "title", "amount", "costCenter" }, groupBy = "costCenter", orderBy = "fields/amount desc", layout = "board", isDefault = true });
        var put = await admin.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{invoices}/documentSettings", new { ocrLanguages = "deu+eng", duplicatePolicy = "block" }, Ct);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(Ct));
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{invoices}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        var replace = await admin.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{invoices}/permissions/grants", new
        {
            grants = new object[]
            {
                new { principalType = "group", principalId = group, level = "contribute" },
                new { principalType = "user", principalId = alice, level = "read" },
            },
        }, Ct);
        Assert.True(replace.IsSuccessStatusCode, await replace.Content.ReadAsStringAsync(Ct));
        return ws;
    }

    [Fact]
    public async Task A_workspace_template_is_applied_to_another_tenant_idempotently()
    {
        await factory.CreateTenantAsync("prv-src");
        await factory.CreateTenantAsync("prv-dst");
        var source = await ApiClient.CreateAsync(factory, "prv-src");
        var target = await ApiClient.CreateAsync(factory, "prv-dst");
        var ws = await BuildFinanceAsync(source);

        var xml = await ExportAsync(source, ws);
        var document = XDocument.Parse(xml);
        Assert.Equal("Workspace", document.Root!.Attribute("Scope")!.Value);
        Assert.Equal(["Invoice", "Vendor"], document.Root.Element(T + "ContentTypes")!.Elements().Select(e => e.Attribute("Name")!.Value));
        Assert.Equal("Finance/Cost centers", document.Descendants(T + "Field").Single(f => f.Attribute("Name")!.Value == "costCenter").Attribute("TermSet")!.Value);
        Assert.Equal("Finance/Vendors", document.Descendants(T + "Field").Single(f => f.Attribute("Name")!.Value == "vendor").Attribute("LookupList")!.Value);
        Assert.Equal("Accountants", Assert.Single(document.Root.Element(T + "Groups")!.Elements()).Attribute("Name")!.Value);
        Assert.Contains("urn:paperdotnet:documents:1", xml, StringComparison.Ordinal);
        Assert.DoesNotContain(ws.ToString(), xml, StringComparison.OrdinalIgnoreCase);

        // Users are referenced by name: alice has to exist on the target.
        await PostIdAsync(target, "/v1.0/users", new { userName = "alice", password = "alice-password-1" });

        var plan = await ApplyOkAsync(target, xml, "?dryRun=true");
        Assert.True(plan.GetProperty("dryRun").GetBoolean());
        var planned = Changes(plan);
        Assert.Contains("create workspace Finance", planned);
        Assert.Contains("create list Finance/Invoices", planned);
        Assert.Contains("create contentType Invoice", planned);
        Assert.Contains("create termSet Finance/Cost centers", planned);
        Assert.Contains("create group Accountants", planned);
        Assert.DoesNotContain(await GetListAsync(target, "/v1.0/workspaces"), w => w.GetProperty("name").GetString() == "Finance");

        var applied = await ApplyOkAsync(target, xml);
        Assert.Equal(planned, Changes(applied));
        Assert.Empty(applied.GetProperty("warnings").EnumerateArray());

        // The lookup points at the new Vendors list, the managed metadata field at the new term set.
        var targetWs = (await GetListAsync(target, "/v1.0/workspaces")).Single(w => w.GetProperty("name").GetString() == "Finance").GetProperty("id").GetGuid();
        var lists = await GetListAsync(target, $"/v1.0/workspaces/{targetWs}/lists");
        var vendorsId = lists.Single(l => l.GetProperty("name").GetString() == "Vendors").GetProperty("id").GetGuid();
        var invoice = (await GetListAsync(target, "/v1.0/contentTypes")).Single(c => c.GetProperty("name").GetString() == "Invoice");
        Assert.Equal(vendorsId, invoice.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "vendor").GetProperty("lookupListId").GetGuid());

        // Applying again changes nothing, and exporting the target gives the same template.
        Assert.Empty(Changes(await ApplyOkAsync(target, xml)));
        Assert.Equal(xml, await ExportAsync(target, targetWs));
    }

    [Fact]
    public async Task Templates_are_validated_with_line_numbers_before_anything_is_written()
    {
        await factory.CreateTenantAsync("prv-valid");
        var admin = await ApiClient.CreateAsync(factory, "prv-valid");
        const string template = """
            <Template xmlns="urn:paperdotnet:template:1" SchemaVersion="1.0" Scope="Workspace">
              <Parameters><Parameter Name="Project" /></Parameters>
              <Groups><Group Name="Team {parameter:Project}" /></Groups>
              <x:Unknown xmlns:x="urn:example:other" />
              <Workspaces>
                <Workspace Name="{parameter:Project}">
                  <Lists>
                    <List Name="Things"><ContentTypes><ContentTypeRef Name="CONTENT_TYPE" /></ContentTypes></List>
                  </Lists>
                </Workspace>
              </Workspaces>
            </Template>
            """;

        async Task<string> ErrorAsync(string xml, string query = "")
        {
            var response = await ApplyAsync(admin, xml, query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return string.Join(" ", (await response.ReadJsonAsync()).GetProperty("errors").GetProperty("template").EnumerateArray().Select(e => e.GetString()));
        }

        Assert.Contains("Line 3: Parameter 'Project' needs a value.", await ErrorAsync(template), StringComparison.Ordinal);
        Assert.Contains("Line 1", await ErrorAsync(template.Replace("SchemaVersion=\"1.0\"", "SchemaVersion=\"1.0\" Bogus=\"x\"", StringComparison.Ordinal), "?parameters[Project]=Apollo"), StringComparison.Ordinal);
        Assert.Contains("Line 3", await ErrorAsync(template.Replace("<Group ", "<Grop ", StringComparison.Ordinal).Replace("<Grop Name=\"Team {parameter:Project}\" />", "<Grop />", StringComparison.Ordinal), "?parameters[Project]=Apollo"), StringComparison.Ordinal);
        Assert.NotEmpty(await ErrorAsync("<!DOCTYPE Template [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" + template));
        Assert.NotEmpty(await ErrorAsync("<Template"));

        // Semantic errors come from the dry run: nothing is written, not even the group before the list.
        Assert.Contains("Line 8: Content type 'CONTENT_TYPE' does not exist", await ErrorAsync(template, "?parameters[Project]=Apollo"), StringComparison.Ordinal);
        Assert.DoesNotContain(await GetListAsync(admin, "/v1.0/groups"), g => g.GetProperty("name").GetString() == "Team Apollo");

        var valid = template.Replace("CONTENT_TYPE", "Item", StringComparison.Ordinal);
        var plan = await ApplyOkAsync(admin, valid, "?dryRun=true&parameters[Project]=Apollo&parameters[Other]=1");
        Assert.Equal(["create group Team Apollo", "create workspace Apollo", "create list Apollo/Things"], Changes(plan));
        var warnings = plan.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("'Other' is not declared", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("Line 4: Section {urn:example:other}Unknown was skipped", StringComparison.Ordinal));
        Assert.Empty(await GetListAsync(admin, "/v1.0/workspaces"));

        Assert.Equal(3, Changes(await ApplyOkAsync(admin, valid, "?parameters[Project]=Apollo")).Count);
        var ws = Assert.Single(await GetListAsync(admin, "/v1.0/workspaces")).GetProperty("id").GetGuid();

        // A workspace template can target an existing workspace (its name stays).
        var other = await admin.CreateWorkspaceAsync("Gemini");
        Assert.Contains("create list Apollo/Things", Changes(await ApplyOkAsync(admin, valid, $"?workspaceId={other}&parameters[Project]=Apollo")));
        Assert.Single(await GetListAsync(admin, $"/v1.0/workspaces/{other}/lists"));
        Assert.NotEqual(ws, other);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await admin.PostAsJsonAsync("/v1.0/provisioning/apply", new { template = valid }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Templates_need_their_scopes_and_stay_in_the_tenant()
    {
        await factory.CreateTenantAsync("prv-acl");
        await factory.CreateTenantAsync("prv-acl-b");
        var admin = await ApiClient.CreateAsync(factory, "prv-acl");
        var foreign = await ApiClient.CreateAsync(factory, "prv-acl-b");
        var ws = await admin.CreateWorkspaceAsync("Secret");
        await PostIdAsync(admin, "/v1.0/users", new { userName = "mia", password = "mia-password-1" });
        var member = await ApiClient.CreateAsync(factory, "prv-acl", "mia", "mia-password-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/v1.0/provisioning/export", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApplyAsync(member, "<Template />")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApplyAsync(foreign, "<Template />", $"?workspaceId={ws}")).StatusCode);
        Assert.DoesNotContain("Secret", await ExportAsync(foreign), StringComparison.Ordinal);

        // Personal workspaces are not part of templates.
        var home = (await (await admin.GetAsync("/v1.0/me/home", Ct)).ReadJsonAsync()).GetProperty("workspaceId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.GetAsync($"/v1.0/provisioning/export?workspaceId={home}", Ct)).StatusCode);
        Assert.DoesNotContain("Inbox", await ExportAsync(admin), StringComparison.Ordinal);

        var schema = await factory.CreateClient().GetAsync("/v1.0/provisioning/schema", Ct);
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
        Assert.Contains("targetNamespace=\"urn:paperdotnet:template:1\"", await schema.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenant_templates_enable_extensions_and_run_their_sections()
    {
        var tenant = await factory.CreateTenantAsync("prv-ext");
        var other = await factory.CreateTenantAsync("prv-ext-b");
        var admin = await ApiClient.CreateAsync(factory, "prv-ext");
        var otherAdmin = await ApiClient.CreateAsync(factory, "prv-ext-b");
        const string template = """
            <Template xmlns="urn:paperdotnet:template:1" SchemaVersion="1.0" Scope="Tenant">
              <Extensions>
                <Extension Id="samples.invoices"><Setting Name="currency">"USD"</Setting></Extension>
              </Extensions>
              <Roles><Role Name="Approvers"><Scope>samples.invoices.approve</Scope><Assignment User="admin" /></Role></Roles>
              <ContentTypes>
                <ContentType Name="Invoice" Key="samples.invoices.invoice" />
                <ContentType Name="Supplier"><Field Name="iban" Type="samples.invoices.iban" /></ContentType>
              </ContentTypes>
              <m:Marker xmlns:m="urn:test:marker" Value="v1" />
            </Template>
            """;

        var plan = await ApplyOkAsync(admin, template, "?dryRun=true");
        Assert.Contains("update extension samples.invoices", Changes(plan));
        Assert.False(TestTemplateHandler.Applied.ContainsKey(tenant.Id));
        Assert.False((await (await admin.GetAsync("/v1.0/extensions/samples.invoices", Ct)).ReadJsonAsync()).GetProperty("enabled").GetBoolean());

        await ApplyOkAsync(admin, template);
        Assert.True((await (await admin.GetAsync("/v1.0/extensions/samples.invoices", Ct)).ReadJsonAsync()).GetProperty("enabled").GetBoolean());
        Assert.Equal("USD", (await (await admin.GetAsync("/v1.0/extensions/samples.invoices/settings", Ct)).ReadJsonAsync()).GetProperty("currency").GetString());
        Assert.Equal("v1", TestTemplateHandler.Applied[tenant.Id]);
        Assert.Contains(await GetListAsync(admin, "/v1.0/contentTypes"), c => c.GetProperty("name").GetString() == "Supplier");

        var exported = XDocument.Parse(await ExportAsync(admin));
        Assert.Equal("\"USD\"", exported.Descendants(T + "Setting").Single().Value);
        Assert.Equal("v1", exported.Root!.Element(TestTemplateHandler.Ns + "Marker")!.Attribute("Value")!.Value);
        Assert.Contains(exported.Descendants(T + "Role"), r => r.Attribute("Name")!.Value == "Approvers");

        // Without the extension, its section is skipped with a warning (and its field type is unknown).
        var marker = """<Template xmlns="urn:paperdotnet:template:1" SchemaVersion="1.0" Scope="Tenant"><m:Marker xmlns:m="urn:test:marker" Value="v2" /></Template>""";
        var skipped = await ApplyOkAsync(otherAdmin, marker);
        Assert.Contains(skipped.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("'samples.invoices' is not enabled", StringComparison.Ordinal));
        Assert.False(TestTemplateHandler.Applied.ContainsKey(other.Id));
        var unknown = await ApplyAsync(otherAdmin, template.Replace("<Extension Id=\"samples.invoices\">", "<Extension Id=\"samples.missing\">", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }
}
