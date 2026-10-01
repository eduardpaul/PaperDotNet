using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PaperDotNet.IntegrationTests;

/// <summary>Provisioning templates (PRV-01…03, PRV-05): export, apply, dry run, parameters, schema.</summary>
public sealed class ProvisioningTests : IAsyncLifetime
{
    private static readonly XNamespace T = "urn:paperdotnet:template:1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<string> ExportAsync(HttpClient client, string? workspaceId = null)
    {
        using var response = await client.GetAsync($"/v1.0/provisioning/export{(workspaceId is null ? "" : $"?workspaceId={workspaceId}")}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        return body;
    }

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, string template, string query = "") =>
        client.PostAsync($"/v1.0/provisioning/apply{query}", new StringContent(template, Encoding.UTF8, "application/xml"), Ct);

    private static async Task<JsonElement> ApplyOkAsync(HttpClient client, string template, string query = "")
    {
        using var response = await ApplyAsync(client, template, query);
        return await response.JsonAsync(HttpStatusCode.OK);
    }

    private static List<string> Changes(JsonElement result) =>
        [.. result.GetProperty("changes").EnumerateArray().Select(c => $"{c.GetProperty("action").GetString()} {c.GetProperty("kind").GetString()} {c.GetProperty("name").GetString()}")];

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

    /// <summary>A workspace with a lookup between lists, managed metadata, a view and unique permissions.</summary>
    private static async Task<string> BuildFinanceAsync(HttpClient admin)
    {
        var alice = await PostIdAsync(admin, "/v1.0/users", new { userName = "alice", password = "alice-password-1" });
        var group = await PostIdAsync(admin, "/v1.0/groups", new { name = "Accountants", description = "Books and invoices" });
        using (var member = await admin.PostAsJsonAsync($"/v1.0/groups/{group}/members", new { userId = alice }, Ct))
        {
            Assert.True(member.IsSuccessStatusCode);
        }

        var termGroup = await PostIdAsync(admin, "/v1.0/termStore/groups", new { name = "Finance" });
        var set = await PostIdAsync(admin, "/v1.0/termStore/sets", new { groupId = termGroup, name = "Cost centers" });
        var sales = await PostIdAsync(admin, $"/v1.0/termStore/sets/{set}/terms", new { name = "Sales", color = "#1f77b4", labels = new[] { new { language = "de", name = "Vertrieb" } } });
        await PostIdAsync(admin, $"/v1.0/termStore/sets/{set}/terms", new { name = "Field sales", parentId = sales, synonyms = new[] { "Outside sales" } });

        var ws = await Api.CreateWorkspaceAsync(admin, "Finance");
        using (var member = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = alice, role = "member" }, Ct))
        {
            Assert.True(member.IsSuccessStatusCode);
        }

        var vendor = await Api.CreateContentTypeAsync(admin, "Vendor", new[] { new { name = "country", displayName = "Country", type = "choice", choices = new[] { "DE", "FR" } } });
        var vendors = await PostIdAsync(admin, $"/v1.0/workspaces/{ws}/lists", new { name = "Vendors", contentTypeIds = new[] { vendor } });
        var invoice = await Api.CreateContentTypeAsync(admin, "Invoice", new object[]
        {
            new { name = "amount", displayName = "Amount", type = "currency", currencyCode = "EUR", required = true },
            new { name = "costCenter", displayName = "costCenter", type = "managedMetadata", termSetId = set },
            new { name = "vendor", displayName = "vendor", type = "lookup", lookupListId = vendors },
        });
        var invoices = await PostIdAsync(admin, $"/v1.0/workspaces/{ws}/lists", new { name = "Invoices", kind = "library", contentTypeIds = new[] { invoice } });
        await PostIdAsync(admin, $"/v1.0/workspaces/{ws}/lists/{invoices}/views",
            new { name = "By cost center", columns = new[] { "title", "amount", "costCenter" }, groupBy = "costCenter", orderBy = "fields/amount desc", layout = "board", isDefault = true });
        using (var broken = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{invoices}/permissions/breakInheritance", new { copyGrants = false }, Ct))
        {
            await broken.JsonAsync(HttpStatusCode.OK);
        }

        using var grants = await admin.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{invoices}/permissions/grants", new
        {
            grants = new object[]
            {
                new { principalType = "group", principalId = group, level = "contribute" },
                new { principalType = "user", principalId = alice, level = "read" },
                new { principalType = "workspaceMembers", principalId = ws, level = "read" },
            },
        }, Ct);
        await grants.JsonAsync(HttpStatusCode.OK);
        return ws;
    }

    [Fact]
    public async Task A_workspace_template_is_applied_to_another_tenant_idempotently()
    {
        var ws = await BuildFinanceAsync(_admin);
        var target = await _host.CreateTenantAsync("prv-dst");

        var xml = await ExportAsync(_admin, ws);
        var document = XDocument.Parse(xml);
        Assert.Equal("Workspace", document.Root!.Attribute("Scope")!.Value);
        Assert.Equal(["Invoice", "Vendor"], document.Root.Element(T + "ContentTypes")!.Elements().Select(e => e.Attribute("Name")!.Value));
        Assert.Equal("Finance/Cost centers", document.Descendants(T + "Field").Single(f => f.Attribute("Name")!.Value == "costCenter").Attribute("TermSet")!.Value);
        Assert.Equal("Finance/Vendors", document.Descendants(T + "Field").Single(f => f.Attribute("Name")!.Value == "vendor").Attribute("LookupList")!.Value);
        Assert.Equal("Accountants", Assert.Single(document.Root.Element(T + "Groups")!.Elements()).Attribute("Name")!.Value);
        Assert.DoesNotContain(ws, xml, StringComparison.OrdinalIgnoreCase);

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

        // The lookup points at the new Vendors list.
        var targetWs = (await GetListAsync(target, "/v1.0/workspaces")).Single(w => w.GetProperty("name").GetString() == "Finance").Id();
        var lists = await GetListAsync(target, $"/v1.0/workspaces/{targetWs}/lists");
        var vendorsId = lists.Single(l => l.GetProperty("name").GetString() == "Vendors").Id();
        var invoice = (await GetListAsync(target, "/v1.0/contentTypes")).Single(c => c.GetProperty("name").GetString() == "Invoice");
        Assert.Equal(vendorsId, invoice.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "vendor").GetProperty("lookupListId").GetString());

        // Applying again changes nothing, and exporting the target gives the same template.
        Assert.Empty(Changes(await ApplyOkAsync(target, xml)));
        Assert.Equal(xml, await ExportAsync(target, targetWs));
    }

    [Fact]
    public async Task Templates_are_validated_with_line_numbers_before_anything_is_written()
    {
        const string template = """
            <Template xmlns="urn:paperdotnet:template:1" SchemaVersion="1.0" Scope="Workspace">
              <Parameters><Parameter Name="Project" /></Parameters>
              <ContentTypes><ContentType Name="Note {parameter:Project}"><Field Name="body" Type="note" /></ContentType></ContentTypes>
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
            using var response = await ApplyAsync(_admin, xml, query);
            var body = await response.JsonAsync(HttpStatusCode.BadRequest);
            return string.Join(" ", body.GetProperty("errors").GetProperty("template").EnumerateArray().Select(e => e.GetString()));
        }

        Assert.Contains("Line 3: Parameter 'Project' needs a value.", await ErrorAsync(template), StringComparison.Ordinal);
        Assert.Contains("Line 1", await ErrorAsync(template.Replace("SchemaVersion=\"1.0\"", "SchemaVersion=\"1.0\" Bogus=\"x\"", StringComparison.Ordinal), "?parameters[Project]=Apollo"), StringComparison.Ordinal);
        Assert.NotEmpty(await ErrorAsync("<!DOCTYPE Template [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" + template));
        Assert.NotEmpty(await ErrorAsync("<Template"));

        // Semantic errors come from the dry run: nothing is written, not even the content type before the list.
        Assert.Contains("Line 8: Content type 'CONTENT_TYPE' does not exist", await ErrorAsync(template, "?parameters[Project]=Apollo"), StringComparison.Ordinal);
        Assert.DoesNotContain(await GetListAsync(_admin, "/v1.0/contentTypes"), c => c.GetProperty("name").GetString() == "Note Apollo");

        var valid = template.Replace("CONTENT_TYPE", "Note {parameter:Project}", StringComparison.Ordinal);
        var plan = await ApplyOkAsync(_admin, valid, "?dryRun=true&parameters[Project]=Apollo&parameters[Other]=1");
        Assert.Equal(["create contentType Note Apollo", "create workspace Apollo", "create list Apollo/Things"], Changes(plan));
        var warnings = plan.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("'Other' is not declared", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("Line 4: Section {urn:example:other}Unknown was skipped", StringComparison.Ordinal));
        Assert.DoesNotContain(await GetListAsync(_admin, "/v1.0/workspaces"), w => w.GetProperty("name").GetString() == "Apollo");

        Assert.Equal(3, Changes(await ApplyOkAsync(_admin, valid, "?parameters[Project]=Apollo")).Count);
        var ws = (await GetListAsync(_admin, "/v1.0/workspaces")).Single(w => w.GetProperty("name").GetString() == "Apollo").Id();

        // A workspace template can target an existing workspace (its name stays).
        var other = await Api.CreateWorkspaceAsync(_admin, "Gemini");
        Assert.Contains("create list Apollo/Things", Changes(await ApplyOkAsync(_admin, valid, $"?workspaceId={other}&parameters[Project]=Apollo")));
        Assert.Single(await GetListAsync(_admin, $"/v1.0/workspaces/{other}/lists"));
        Assert.NotEqual(ws, other);

        using var json = await _admin.PostAsJsonAsync("/v1.0/provisioning/apply", new { template = valid }, Ct);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);
    }

    [Fact]
    public async Task Templates_need_their_scopes_and_stay_in_the_tenant()
    {
        var foreign = await _host.CreateTenantAsync("prv-acl-b");
        var ws = await Api.CreateWorkspaceAsync(_admin, "Secret");
        await PostIdAsync(_admin, "/v1.0/users", new { userName = "mia", password = "mia-password-1" });
        var member = await _host.SignInAsync("mia", "mia-password-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/v1.0/provisioning/export", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApplyAsync(member, "<Template />")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApplyAsync(foreign, "<Template />", $"?workspaceId={ws}")).StatusCode);
        Assert.DoesNotContain("Secret", await ExportAsync(foreign), StringComparison.Ordinal);

        using var schema = await _host.CreateClient().GetAsync("/v1.0/provisioning/schema", Ct);
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
        Assert.Contains("targetNamespace=\"urn:paperdotnet:template:1\"", await schema.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenant_templates_enable_extensions_and_run_their_sections()
    {
        var other = await _host.CreateTenantAsync("prv-ext-b");
        var tenant = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
        const string template = """
            <Template xmlns="urn:paperdotnet:template:1" SchemaVersion="1.0" Scope="Tenant">
              <Extensions>
                <Extension Id="tests.tickets"><Setting Name="prefix">"SUP-"</Setting></Extension>
              </Extensions>
              <Roles><Role Name="Ticket admins"><Scope>tests.tickets.admin</Scope><Assignment User="admin" /></Role></Roles>
              <ContentTypes>
                <ContentType Name="Ticket" Key="tests.tickets.ticket" />
                <ContentType Name="Incident"><Field Name="code" Type="tests.tickets.code" /></ContentType>
              </ContentTypes>
              <m:Marker xmlns:m="urn:test:marker" Value="v1" />
            </Template>
            """;

        var plan = await ApplyOkAsync(_admin, template, "?dryRun=true");
        Assert.Contains("update extension tests.tickets", Changes(plan));
        Assert.False(Extension.TicketMarkerSection.Applied.ContainsKey(tenant));
        Assert.False((await (await _admin.GetAsync("/v1.0/extensions/tests.tickets", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("enabled").GetBoolean());

        await ApplyOkAsync(_admin, template);
        Assert.True((await (await _admin.GetAsync("/v1.0/extensions/tests.tickets", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("enabled").GetBoolean());
        Assert.Equal("SUP-", (await (await _admin.GetAsync("/v1.0/extensions/tests.tickets/settings", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("prefix").GetString());
        Assert.Equal("v1", Extension.TicketMarkerSection.Applied[tenant]);
        Assert.Contains(await GetListAsync(_admin, "/v1.0/contentTypes"), c => c.GetProperty("name").GetString() == "Incident");

        var exported = XDocument.Parse(await ExportAsync(_admin));
        Assert.Equal("\"SUP-\"", exported.Descendants(T + "Setting").Single().Value);
        Assert.Equal("v1", exported.Root!.Element(Extension.TicketMarkerSection.Ns + "Marker")!.Attribute("Value")!.Value);
        Assert.Contains(exported.Descendants(T + "Role"), r => r.Attribute("Name")!.Value == "Ticket admins");

        // Without the extension, its section is skipped with a warning; an unknown extension is an error.
        var marker = """<Template xmlns="urn:paperdotnet:template:1" SchemaVersion="1.0" Scope="Tenant"><m:Marker xmlns:m="urn:test:marker" Value="v2" /></Template>""";
        var skipped = await ApplyOkAsync(other, marker);
        Assert.Contains(skipped.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("'tests.tickets' is not enabled", StringComparison.Ordinal));
        using var unknown = await ApplyAsync(other, template.Replace("<Extension Id=\"tests.tickets\">", "<Extension Id=\"tests.missing\">", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Workflows_and_smart_folders_travel_with_workspace_templates()
    {
        var group = await PostIdAsync(_admin, "/v1.0/termStore/groups", new { name = "Work" });
        var projects = await PostIdAsync(_admin, "/v1.0/termStore/sets", new { groupId = group, name = "Projects" });
        var apollo = await PostIdAsync(_admin, $"/v1.0/termStore/sets/{projects}/terms", new { name = "Apollo" });
        var ws = await Api.CreateWorkspaceAsync(_admin, "Space");
        var jobs = (await Api.CreateListAsync(_admin, ws, "Jobs", new object[]
        {
            new { name = "project", displayName = "Project", type = "managedMetadata", termSetId = projects },
            new { name = "status", displayName = "Status", type = "choice", choices = new[] { "open", "done" } },
        })).Id();
        await PostIdAsync(_admin, "/v1.0/smartFolders", new
        {
            name = "Apollo work",
            workspaceId = ws,
            definition = new { lists = new[] { "Jobs" }, terms = new[] { apollo }, filter = "fields/status eq 'open'" },
        });
        await PostIdAsync(_admin, "/v1.0/smartFolders", new { name = "Private", personal = true, workspaceId = ws, definition = new { } });
        await PostIdAsync(_admin, $"/v1.0/workspaces/{ws}/workflows", new
        {
            name = "Close done jobs",
            definition = JsonNode.Parse("""
                {"trigger":{"type":"manual"},"flow":{"start":"count","nodes":{"count":{"activity":"script","inputs":{"code":"return 1;"}}}}}
                """),
        });

        var xml = await ExportAsync(_admin, ws);
        Assert.Contains("urn:paperdotnet:smartfolders:1", xml, StringComparison.Ordinal);
        Assert.Contains("Work/Projects/Apollo", xml, StringComparison.Ordinal);
        Assert.Contains("urn:paperdotnet:workflow:1", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", xml, StringComparison.Ordinal);

        var other = await _host.CreateTenantAsync("prv-smart-b");
        await ApplyOkAsync(other, xml);
        var folder = Assert.Single((await GetListAsync(other, "/v1.0/smartFolders")));
        Assert.Equal("Apollo work", folder.GetProperty("name").GetString());
        Assert.NotEqual(apollo, folder.GetProperty("definition").GetProperty("terms")[0].GetString());
        var workspace = folder.GetProperty("workspaceId").GetString()!;
        var otherJobs = (await GetListAsync(other, $"/v1.0/workspaces/{workspace}/lists")).Single(l => l.GetProperty("name").GetString() == "Jobs").Id();
        await Api.CreateItemAsync(other, workspace, otherJobs, new { title = "Imported", project = "Apollo", status = "open" });
        var items = await GetListAsync(other, $"/v1.0/smartFolders/{folder.Id()}/items");
        Assert.Equal("Imported", Assert.Single(items).GetProperty("item").GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("Close done jobs", Assert.Single(await GetListAsync(other, $"/v1.0/workspaces/{workspace}/workflows")).GetProperty("name").GetString());

        // Applying again changes nothing.
        Assert.Empty(Changes(await ApplyOkAsync(other, xml)));
        Assert.NotEqual(jobs, otherJobs);
    }
}
