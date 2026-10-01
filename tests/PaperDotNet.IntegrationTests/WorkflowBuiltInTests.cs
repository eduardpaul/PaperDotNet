using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Built-in workflows (EVT-12): the catalog, parameters, copies, the release sync and templates.</summary>
public sealed class WorkflowBuiltInTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private Guid _tenant;
    private string _workspace = "";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _tenant = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Office");
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<JsonElement> GetAsync(HttpClient client, string url) =>
        await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);

    private static JsonElement BuiltIn(JsonElement catalog, string key) => catalog.EnumerateArray().Single(b => b.GetProperty("key").GetString() == key);

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Built_in_workflows_are_turned_on_with_parameters_copied_synced_and_exported()
    {
        var workflows = $"/v1.0/workspaces/{_workspace}/workflows";
        var builtIns = $"{workflows}/builtIns";
        var type = await Api.CreateContentTypeAsync(_admin, "Request", new[] { new { name = "status", displayName = "Status", type = "choice", choices = new[] { "Approved", "Rejected" } } });
        using (var created = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/lists", new { name = "Requests", contentTypeIds = new[] { type } }, Ct))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var requests = (await GetAsync(_admin, $"/v1.0/workspaces/{_workspace}/lists")).EnumerateArray().Single(l => l.GetProperty("name").GetString() == "Requests").Id();

        // The catalog: what modules ship, with parameters and whether the server can run them.
        var approve = BuiltIn(await GetAsync(_admin, builtIns), "workflows.approveItems");
        Assert.False(approve.GetProperty("enabled").GetBoolean());
        Assert.True(approve.GetProperty("available").GetBoolean());
        Assert.Contains("approvers", approve.GetProperty("parameters").GetProperty("required").ToString(), StringComparison.Ordinal);

        // Parameters are checked like a saved workflow; once it was turned on, changes need its ETag (from the catalog).
        async Task<HttpResponseMessage> SetAsync(string key, object body)
        {
            var etag = (await GetAsync(_admin, builtIns)).EnumerateArray().Where(b => b.GetProperty("key").GetString() == key)
                .Select(b => b.TryGetProperty("@odata.etag", out var tag) ? tag.GetString() : null).FirstOrDefault();
            return etag is null
                ? await _admin.PutAsJsonAsync($"{builtIns}/{key}", body, Ct)
                : await _admin.SendAsync(Api.WithETag(HttpMethod.Put, $"{builtIns}/{key}", body, etag), Ct);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync("workflows.approveItems", new { enabled = true, parameters = new { list = "Requests" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync("workflows.approveItems", new { enabled = true, parameters = new { list = "Nope", approvers = new[] { "creator" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync("nope.nothing", new { enabled = true })).StatusCode);
        using var enabled = await SetAsync("workflows.approveItems", new { enabled = true, parameters = new { list = "Requests", approvers = new[] { "creator" } } });
        var state = await enabled.JsonAsync(HttpStatusCode.OK);
        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.Equal("status", state.GetProperty("values").GetProperty("statusField").GetString()); // the default
        var workflowId = state.GetProperty("workflowId").GetString()!;
        Assert.NotNull(enabled.Headers.ETag);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await _admin.PutAsJsonAsync($"{builtIns}/workflows.approveItems", new { enabled = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed,
            (await _admin.SendAsync(Api.WithETag(HttpMethod.Put, $"{builtIns}/workflows.approveItems", new { enabled = false }, "\"999999\""), Ct)).StatusCode);

        // It runs like any workflow: a new item waits for approval, and the decision sets its status.
        var item = (await Api.CreateItemAsync(_admin, _workspace, requests, new { title = "New laptop" })).Id();
        string? approval = null;
        await WaitAsync(async () => (approval = (await GetAsync(_admin, "/v1.0/me/approvals?status=pending")).GetProperty("value").EnumerateArray()
            .Select(a => a.Id()).FirstOrDefault()) is not null);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/v1.0/me/approvals/{approval}/decision", new { outcome = "approved" }, Ct)).StatusCode);
        await WaitAsync(async () => (await GetAsync(_admin, $"{Api.Items(_workspace, requests)}/{item}")).GetProperty("fields").TryGetProperty("status", out var status)
            && status.GetString() == "Approved");

        // Listed with the workspace's workflows, marked built-in, and read-only.
        var listed = (await GetAsync(_admin, workflows)).GetProperty("value").EnumerateArray().Single(w => w.Id() == workflowId);
        Assert.Equal("workflows.approveItems", listed.GetProperty("builtIn").GetString());
        Assert.Equal("Approve new items", listed.GetProperty("name").GetString());
        var etag = (await _admin.GetAsync($"{workflows}/{workflowId}", Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.SendAsync(Api.Patch($"{workflows}/{workflowId}", new { name = "Mine" }, etag), Ct)).StatusCode);

        // The release sync keeps an unchanged definition.
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await ActivatorUtilities.CreateInstance<BuiltInSyncJob>(scope.ServiceProvider).RunAsync(_tenant, Ct);
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var id = Guid.Parse(workflowId);
            Assert.Equal(1, (await db.Workflows.AsNoTracking().SingleAsync(w => w.TenantId == _tenant && w.Id == id, Ct)).CurrentVersion);
        }

        // Turning it off keeps its parameters; a copy is a workflow of the workspace (the built-in one is turned off).
        using (var off = await SetAsync("workflows.approveItems", new { enabled = false }))
        {
            var offState = await off.JsonAsync(HttpStatusCode.OK);
            Assert.False(offState.GetProperty("enabled").GetBoolean());
            Assert.Equal("Requests", offState.GetProperty("values").GetProperty("list").GetString());
        }

        Assert.True((await SetAsync("workflows.approveItems", new { enabled = true })).IsSuccessStatusCode);
        using (var copied = await _admin.PostAsJsonAsync($"{builtIns}/workflows.approveItems/copy", new { name = "Approve requests" }, Ct))
        {
            var copy = await copied.JsonAsync(HttpStatusCode.Created);
            Assert.Equal("workflows.approveItems", copy.GetProperty("copiedFrom").GetString());
            Assert.Equal("Requests", copy.GetProperty("definition").GetProperty("trigger").GetProperty("list").GetString());
            Assert.False(copy.TryGetProperty("builtIn", out _));
        }

        Assert.False(BuiltIn(await GetAsync(_admin, builtIns), "workflows.approveItems").GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsJsonAsync($"{builtIns}/workflows.approveItems/copy", new { name = "Approve requests" }, Ct)).StatusCode);

        // Another organization sees none of it.
        var foreign = await _host.CreateTenantAsync("builtins-other");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(builtIns, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PutAsJsonAsync($"{builtIns}/workflows.approveItems", new { enabled = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"{builtIns}/workflows.approveItems/copy", new { name = "Mine" }, Ct)).StatusCode);

        // Templates carry the built-in workflow as its key and parameters, and the copy with where it came from.
        var xml = await (await _admin.GetAsync($"/v1.0/provisioning/export?workspaceId={_workspace}", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("BuiltIn=\"workflows.approveItems\"", xml, StringComparison.Ordinal);
        Assert.Contains("CopiedFrom=\"workflows.approveItems\"", xml, StringComparison.Ordinal);
        Task<HttpResponseMessage> ApplyAsync(string template) =>
            foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(template, Encoding.UTF8, "application/xml"), Ct);
        using (var apply = await ApplyAsync(xml))
        {
            await apply.JsonAsync(HttpStatusCode.OK);
        }

        var foreignWs = (await GetAsync(foreign, "/v1.0/workspaces")).GetProperty("value").EnumerateArray().Single(w => w.GetProperty("name").GetString() == "Office").Id();
        var imported = BuiltIn(await GetAsync(foreign, $"/v1.0/workspaces/{foreignWs}/workflows/builtIns"), "workflows.approveItems");
        Assert.Equal("Requests", imported.GetProperty("values").GetProperty("list").GetString());
        Assert.False(imported.GetProperty("enabled").GetBoolean());
        var importedCopy = (await GetAsync(foreign, $"/v1.0/workspaces/{foreignWs}/workflows")).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "Approve requests");
        Assert.Equal("workflows.approveItems", importedCopy.GetProperty("copiedFrom").GetString());
        using (var again = await ApplyAsync(xml))
        {
            Assert.Empty((await again.JsonAsync(HttpStatusCode.OK)).GetProperty("changes").EnumerateArray());
        }

        // A built-in workflow this server does not have is skipped with a warning; the rest still applies.
        var unknown = xml.Replace("BuiltIn=\"workflows.approveItems\"", "BuiltIn=\"gone.workflow\"", StringComparison.Ordinal);
        using (var skipped = await ApplyAsync(unknown))
        {
            Assert.Contains((await skipped.JsonAsync(HttpStatusCode.OK)).GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("gone.workflow", StringComparison.Ordinal));
        }

        // The release sync turns off a built-in workflow the release no longer has.
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var id = Guid.Parse(workflowId);
            var row = await db.Workflows.SingleAsync(w => w.TenantId == _tenant && w.Id == id, Ct);
            row.BuiltInKey = "gone.workflow";
            row.Enabled = true;
            await db.SaveChangesAsync(Ct);
            await ActivatorUtilities.CreateInstance<BuiltInSyncJob>(scope.ServiceProvider).RunAsync(_tenant, Ct);
            Assert.False((await db.Workflows.AsNoTracking().SingleAsync(w => w.TenantId == _tenant && w.Id == id, Ct)).Enabled);
        }
    }
}
