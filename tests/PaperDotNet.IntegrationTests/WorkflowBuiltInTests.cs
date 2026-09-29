using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>Built-in workflows (ADR-0036 slice 9d, EVT-12): the catalog, turning them on with parameters, copying, the release sync and templates.</summary>
public sealed class WorkflowBuiltInTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.ReadJsonAsync();
    }

    private static JsonElement BuiltIn(JsonElement catalog, string key) => catalog.EnumerateArray().Single(b => b.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Built_in_workflows_are_enabled_with_parameters_copied_synced_and_exported()
    {
        var tenant = await factory.CreateTenantAsync("wf-builtin");
        var admin = await ApiClient.CreateAsync(factory, "wf-builtin");
        var ws = await admin.CreateWorkspaceAsync("Office");
        var workflows = $"/v1.0/workspaces/{ws}/workflows";
        var builtIns = $"{workflows}/builtIns";
        var type = await admin.CreateContentTypeAsync("Request", [new { name = "status", type = "choice", choices = new[] { "Approved", "Rejected" } }]);
        var requests = await admin.CreateListAsync(ws, "Requests", type);

        // The catalog: what modules ship, with parameters and whether the server can run them.
        var catalog = await GetAsync(admin, builtIns);
        var approve = BuiltIn(catalog, "workflows.approveItems");
        Assert.False(approve.GetProperty("enabled").GetBoolean());
        Assert.True(approve.GetProperty("available").GetBoolean());
        Assert.Contains("approvers", approve.GetProperty("parameters").GetProperty("required").ToString(), StringComparison.Ordinal);
        Assert.Equal("ai", BuiltIn(catalog, "documents.classify").GetProperty("requires").GetString());
        Assert.True(BuiltIn(catalog, "documents.extract").GetProperty("available").GetBoolean());

        // Parameters are checked like a saved workflow.
        async Task<HttpResponseMessage> SetAsync(string key, object body) => await admin.PutAsJsonAsync($"{builtIns}/{key}", body, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync("workflows.approveItems", new { enabled = true, parameters = new { list = "Requests" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync("workflows.approveItems", new { enabled = true, parameters = new { list = "Nope", approvers = new[] { "creator" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync("nope.nothing", new { enabled = true })).StatusCode);
        var enabled = await SetAsync("workflows.approveItems", new { enabled = true, parameters = new { list = "Requests", approvers = new[] { "creator" } } });
        Assert.True(enabled.IsSuccessStatusCode, await enabled.Content.ReadAsStringAsync(Ct));
        var state = await enabled.ReadJsonAsync();
        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.Equal("status", state.GetProperty("values").GetProperty("statusField").GetString()); // the default
        var workflowId = state.GetProperty("workflowId").GetGuid();

        // It runs like any workflow: a new item waits for approval, and the decision sets its status.
        var item = (await admin.CreateItemAsync(ws, requests, new { fields = new { title = "New laptop" } })).GetProperty("id").GetGuid();
        var approval = await Eventually.WaitForAsync(async () =>
            (await GetAsync(admin, "/v1.0/me/approvals")).GetProperty("value").EnumerateArray().Select(a => (Guid?)a.GetProperty("id").GetGuid()).FirstOrDefault(),
            TimeSpan.FromSeconds(30));
        Assert.True((await admin.PostAsJsonAsync($"/v1.0/me/approvals/{approval}/decision", new { outcome = "approved" }, Ct)).IsSuccessStatusCode);
        await Eventually.WaitForAsync(async () =>
            (await GetAsync(admin, $"/v1.0/workspaces/{ws}/lists/{requests}/items/{item}")).GetProperty("fields").TryGetProperty("status", out var status)
                && status.GetString() == "Approved" ? true : (bool?)null, TimeSpan.FromSeconds(30));

        // Listed with the workspace's workflows, marked built-in, and read-only.
        var listed = (await GetAsync(admin, workflows)).EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == workflowId);
        Assert.Equal("workflows.approveItems", listed.GetProperty("builtIn").GetString());
        Assert.Equal("Approve new items", listed.GetProperty("name").GetString());
        var current = await admin.GetAsync($"{workflows}/{workflowId}", Ct);
        var replace = await admin.SendWithEtagAsync(HttpMethod.Put, $"{workflows}/{workflowId}", current.Headers.ETag!.Tag,
            new { name = "Approve new items", trigger = new { type = "manual" }, steps = new object[] { new { type = "delay", hours = 1 } } });
        Assert.Equal(HttpStatusCode.Conflict, replace.StatusCode);

        // The release sync keeps an unchanged definition, and turns off one the release no longer has.
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            await scope.ServiceProvider.GetRequiredService<BuiltInSyncJob>().RunAsync(Ct);
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            Assert.Equal(1, (await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == workflowId, Ct)).CurrentVersion);
        }

        // Turning it off keeps its parameters; a copy is a workflow of the workspace (the built-in one is turned off).
        var off = await (await SetAsync("workflows.approveItems", new { enabled = false })).ReadJsonAsync();
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal("Requests", off.GetProperty("values").GetProperty("list").GetString());
        Assert.True((await SetAsync("workflows.approveItems", new { enabled = true })).IsSuccessStatusCode);
        var copied = await admin.PostAsJsonAsync($"{builtIns}/workflows.approveItems/copy", new { name = "Approve requests" }, Ct);
        Assert.Equal(HttpStatusCode.Created, copied.StatusCode);
        var copy = await copied.ReadJsonAsync();
        Assert.Equal("workflows.approveItems", copy.GetProperty("copiedFrom").GetString());
        Assert.Equal("Requests", copy.GetProperty("trigger").GetProperty("list").GetString());
        Assert.False(copy.TryGetProperty("builtIn", out _));
        Assert.False(BuiltIn(await GetAsync(admin, builtIns), "workflows.approveItems").GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{builtIns}/workflows.approveItems/copy", new { name = "Approve requests" }, Ct)).StatusCode);

        // Another organization sees none of it.
        await factory.CreateTenantAsync("wf-builtin-b");
        var foreign = await ApiClient.CreateAsync(factory, "wf-builtin-b");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(builtIns, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PutAsJsonAsync($"{builtIns}/workflows.approveItems", new { enabled = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"{builtIns}/workflows.approveItems/copy", new { name = "Mine" }, Ct)).StatusCode);

        // Templates carry the built-in workflow as its key and parameters, and the copy with where it came from.
        var xml = await (await admin.GetAsync($"/v1.0/provisioning/export?workspaceId={ws}", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("BuiltIn=\"workflows.approveItems\"", xml, StringComparison.Ordinal);
        Assert.Contains("CopiedFrom=\"workflows.approveItems\"", xml, StringComparison.Ordinal);
        var apply = await foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.True(apply.IsSuccessStatusCode, await apply.Content.ReadAsStringAsync(Ct));
        var foreignWs = (await GetAsync(foreign, "/v1.0/workspaces")).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "Office").GetProperty("id").GetGuid();
        var imported = BuiltIn(await GetAsync(foreign, $"/v1.0/workspaces/{foreignWs}/workflows/builtIns"), "workflows.approveItems");
        Assert.Equal("Requests", imported.GetProperty("values").GetProperty("list").GetString());
        Assert.False(imported.GetProperty("enabled").GetBoolean());
        var importedCopy = (await GetAsync(foreign, $"/v1.0/workspaces/{foreignWs}/workflows")).EnumerateArray().Single(w => w.GetProperty("name").GetString() == "Approve requests");
        Assert.Equal("workflows.approveItems", importedCopy.GetProperty("copiedFrom").GetString());
        var again = await foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.Empty((await again.ReadJsonAsync()).GetProperty("changes").EnumerateArray());

        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            await db.Workflows.Where(w => w.Id == workflowId).ExecuteUpdateAsync(u => u.SetProperty(w => w.BuiltInKey, "gone.workflow").SetProperty(w => w.Enabled, true), Ct);
            await scope.ServiceProvider.GetRequiredService<BuiltInSyncJob>().RunAsync(Ct);
            Assert.False((await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == workflowId, Ct)).Enabled);
        }
    }

    [Fact]
    public async Task Extract_fields_reads_new_documents_of_a_library()
    {
        await factory.CreateTenantAsync("wf-builtin-docs");
        var admin = await ApiClient.CreateAsync(factory, "wf-builtin-docs");
        var ws = await admin.CreateWorkspaceAsync("Archive");
        var created = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Scans", templateKey = "documents" }, Ct);
        var library = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
        var set = await admin.PutAsJsonAsync($"/v1.0/workspaces/{ws}/workflows/builtIns/documents.extract",
            new { enabled = true, parameters = new { library = "Scans", fields = new[] { "description" } } }, Ct);
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync(Ct));

        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddText("description: Invoice for the walrus", 12, new PdfPoint(40, 760), builder.AddStandard14Font(Standard14Font.Helvetica));
        var upload = await admin.PostAsync($"/v1.0/workspaces/{ws}/lists/{library}/documents",
            new MultipartFormDataContent { { new ByteArrayContent(builder.Build()), "file", "walrus.pdf" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var document = (await upload.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        await Eventually.WaitForAsync(async () =>
            (await GetAsync(admin, $"/v1.0/workspaces/{ws}/lists/{library}/items/{document}")).GetProperty("fields").TryGetProperty("description", out var value)
                && value.GetString() == "Invoice for the walrus" ? true : (bool?)null, TimeSpan.FromSeconds(60));
    }
}
