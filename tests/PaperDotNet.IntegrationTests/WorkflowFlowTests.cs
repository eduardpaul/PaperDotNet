using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Workflow flow parity (ADR-0036/0038): steps, loops, retries, workflow events, restores, concurrency and cleanup.</summary>
public sealed class WorkflowFlowTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private Guid _tenant;
    private string _workspace = "";
    private string _bills = "";
    private string _tasks = "";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _tenant = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Office");
        _bills = (await Api.CreateListAsync(_admin, _workspace, "Bills", new object[] { new { name = "state", type = "text" }, new { name = "amount", type = "number" } })).Id();
        _tasks = (await Api.CreateListAsync(_admin, _workspace, "Tasks", new object[] { new { name = "bill", type = "text" } })).Id();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private string Workflows => $"/v1.0/workspaces/{_workspace}/workflows";

    private async Task<JsonElement> CreateWorkflowAsync(string name, string definition)
    {
        using var response = await _admin.PostAsJsonAsync(Workflows, new { name, definition = JsonNode.Parse(definition) }, Ct);
        return await response.JsonAsync(HttpStatusCode.Created);
    }

    private async Task<string> StartAsync(string workflow, string? item = null, object? inputs = null)
    {
        using var response = await _admin.PostAsJsonAsync($"{Workflows}/{workflow}/runs", new { listId = item is null ? null : _bills, itemId = item, inputs }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Accepted)).Id();
    }

    private async Task<JsonElement> RunAsync(string run, params string[] statuses)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var body = await (await _admin.GetAsync($"{Workflows}/runs/{run}", Ct)).JsonAsync(HttpStatusCode.OK);
            if (statuses.Contains(body.GetProperty("status").GetString()))
            {
                return body;
            }

            Assert.True(DateTime.UtcNow < deadline, body.ToString());
            await Task.Delay(100, Ct);
        }
    }

    private async Task<List<JsonElement>> RunsOfAsync(string workflow, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var runs = (await (await _admin.GetAsync($"{Workflows}/{workflow}/runs", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray().ToList();
            if (runs.Count >= count && runs.All(r => r.GetProperty("status").GetString() is "completed" or "failed" or "cancelled"))
            {
                return runs;
            }

            Assert.True(DateTime.UtcNow < deadline, $"{runs.Count} runs");
            await Task.Delay(100, Ct);
        }
    }

    private async Task<JsonElement> FieldsAsync(string list, string item) =>
        (await (await _admin.GetAsync($"{Api.Items(_workspace, list)}/{item}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("fields");

    private async Task<List<string?>> TitlesAsync(string list) =>
        [.. (await (await _admin.GetAsync(Api.Items(_workspace, list), Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
            .Select(i => i.GetProperty("fields").GetProperty("title").GetString()).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Steps_compile_to_a_flow_with_approvals_and_conditions()
    {
        var workflow = (await CreateWorkflowAsync("Bill approval", """
            {
              "trigger": { "type": "manual", "list": "Bills" },
              "steps": [
                { "type": "approval", "name": "Manager", "assignees": ["admin"], "title": "Approve {title}" },
                { "type": "condition", "step": "Manager", "is": "approved",
                  "then": [ { "type": "action", "action": "item.update", "inputs": { "fields": { "state": "Approved" } } } ],
                  "else": [ { "type": "action", "action": "item.update", "inputs": { "fields": { "state": "Rejected" } } } ] },
                { "type": "action", "action": "item.create", "inputs": { "list": "Tasks", "fields": { "title": "Pay {title}" } } }
              ]
            }
            """)).Id();
        var bill = (await Api.CreateItemAsync(_admin, _workspace, _bills, new { title = "Bill 1" })).Id();
        var run = await StartAsync(workflow, bill);
        await RunAsync(run, "waiting");
        var approval = (await (await _admin.GetAsync("/v1.0/me/approvals?status=pending", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value")[0].Id();
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/v1.0/me/approvals/{approval}/decision", new { outcome = "rejected" }, Ct)).StatusCode);
        await RunAsync(run, "completed");
        Assert.Equal("Rejected", (await FieldsAsync(_bills, bill)).GetProperty("state").GetString());
        Assert.Equal(["Pay Bill 1"], await TitlesAsync(_tasks));

        // Steps and flows do not mix; conditions name earlier approvals.
        using var both = await _admin.PostAsJsonAsync(Workflows, new { name = "Both", definition = JsonNode.Parse("""
            { "trigger": { "type": "manual" }, "steps": [ { "type": "delay", "hours": 1 } ], "flow": { "start": "a", "nodes": { "a": { "activity": "end" } } } }
            """) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        using var unknown = await _admin.PostAsJsonAsync(Workflows, new { name = "Unknown", definition = JsonNode.Parse("""
            { "trigger": { "type": "manual" }, "steps": [ { "type": "condition", "step": "Nobody", "is": "approved" } ] }
            """) }, Ct);
        Assert.Contains("not an earlier approval step", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForEach_loops_over_arrays_and_queries_with_nested_loops()
    {
        await Api.CreateItemAsync(_admin, _workspace, _bills, new { title = "Small", amount = 5 });
        await Api.CreateItemAsync(_admin, _workspace, _bills, new { title = "Big", amount = 500 });
        var workflow = (await CreateWorkflowAsync("Loops", """
            {
              "trigger": { "type": "manual" },
              "variables": { "people": ["ann", "bo"] },
              "flow": { "start": "bills", "nodes": {
                "bills": { "activity": "forEach", "inputs": { "query": { "list": "Bills", "filter": "fields/amount gt 1" }, "as": "bill" },
                           "next": { "item": "people", "done": "count" } },
                "people": { "activity": "forEach", "inputs": { "items": "{var:people}", "as": "person" }, "next": { "item": "task", "done": "bills" } },
                "task": { "activity": "item.create", "inputs": { "list": "Tasks", "fields": { "title": "{var:person}: {var:bill.title}", "bill": "{var:bill.id}" } },
                          "next": { "done": "people" } },
                "count": { "activity": "setVariable", "inputs": { "name": "loops", "value": "{step:bills.count}" } }
              } }
            }
            """)).Id();
        var run = await RunAsync(await StartAsync(workflow), "completed", "failed");
        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal(["ann: Big", "ann: Small", "bo: Big", "bo: Small"], await TitlesAsync(_tasks));
        Assert.Equal(2, run.GetProperty("variables").GetProperty("loops").GetInt32());

        // A loop inside a loop needs its own variable; forEach needs an item port.
        using var same = await _admin.PostAsJsonAsync(Workflows, new { name = "Same", definition = JsonNode.Parse("""
            { "trigger": { "type": "manual" }, "flow": { "start": "a", "nodes": {
              "a": { "activity": "forEach", "inputs": { "items": [1, 2] }, "next": { "item": "b" } },
              "b": { "activity": "forEach", "inputs": { "items": [3] }, "next": { "item": "a" } } } } }
            """) }, Ct);
        Assert.Contains("needs its own variable", await same.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        using var noBody = await _admin.PostAsJsonAsync(Workflows, new { name = "No body", definition = JsonNode.Parse("""
            { "trigger": { "type": "manual" }, "flow": { "start": "a", "nodes": { "a": { "activity": "forEach", "inputs": { "items": [1] } } } } }
            """) }, Ct);
        Assert.Contains("needs an item port", await noBody.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failing_nodes_are_retried_then_take_their_error_port_or_fail_the_run()
    {
        var retried = (await CreateWorkflowAsync("Retried", """
            {
              "trigger": { "type": "manual" },
              "flow": { "start": "write", "nodes": {
                "write": { "activity": "item.create", "inputs": { "list": "Nowhere", "fields": { "title": "x" } },
                           "retry": { "attempts": 2, "delayMinutes": 0 }, "next": { "error": "note" } },
                "note": { "activity": "item.create", "inputs": { "list": "Tasks", "fields": { "title": "failed: {step:write.error}" } } }
              } }
            }
            """)).Id();
        var run = await RunAsync(await StartAsync(retried), "completed", "failed");
        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal(2, run.GetProperty("log").EnumerateArray().Count(l => l.GetProperty("message").GetString()!.Contains("trying again", StringComparison.Ordinal)));
        Assert.StartsWith("failed: ", Assert.Single(await TitlesAsync(_tasks)), StringComparison.Ordinal);

        // Without an error port the run fails at the node, and can be retried from there.
        var failing = (await CreateWorkflowAsync("Failing", """
            { "trigger": { "type": "manual" }, "flow": { "start": "write", "nodes": {
              "write": { "activity": "item.create", "inputs": { "list": "Later", "fields": { "title": "x" } } } } } }
            """)).Id();
        var failedRun = await StartAsync(failing);
        var failed = await RunAsync(failedRun, "failed");
        Assert.Equal("write", failed.GetProperty("failedNode").GetString());
        await Api.CreateListAsync(_admin, _workspace, "Later");
        using (var retry = await _admin.PostAsync($"{Workflows}/runs/{failedRun}/retry", null, Ct))
        {
            await retry.JsonAsync(HttpStatusCode.OK);
        }

        await RunAsync(failedRun, "completed");
        using var again = await _admin.PostAsync($"{Workflows}/runs/{failedRun}/retry", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Workflows_follow_other_workflows_by_their_events()
    {
        var source = await CreateWorkflowAsync("Check bill!", """
            {
              "trigger": { "type": "manual", "list": "Bills" },
              "flow": { "start": "check", "nodes": {
                "check": { "activity": "if", "inputs": { "left": "{amount}", "op": "gt", "right": "100" }, "next": { "true": "raise" } },
                "raise": { "activity": "event.raise", "inputs": { "event": "tooBig", "data": { "amount": "{amount}", "note": "over {amount}" } } }
              } }
            }
            """);
        Assert.Equal("check-bill", source.GetProperty("key").GetString());
        var onEvent = (await CreateWorkflowAsync("On too big", """
            { "trigger": { "type": "wf.check-bill.tooBig" }, "flow": { "start": "mark", "nodes": {
              "mark": { "activity": "item.update", "inputs": { "fields": { "state": "{data:note}" } } } } } }
            """)).Id();
        var onCompleted = (await CreateWorkflowAsync("On done", """
            { "trigger": { "type": "wf.check-bill.completed" }, "flow": { "start": "task", "nodes": {
              "task": { "activity": "item.create", "inputs": { "list": "Tasks", "fields": { "title": "checked {title}", "bill": "{data:runId}" } } } } } }
            """)).Id();

        // A rename keeps the key.
        var etag = (await _admin.GetAsync($"{Workflows}/{source.Id()}", Ct)).Headers.ETag!.Tag;
        using (var renamed = await _admin.SendAsync(Api.Patch($"{Workflows}/{source.Id()}", new { name = "Bill check" }, etag), Ct))
        {
            Assert.Equal("check-bill", (await renamed.JsonAsync(HttpStatusCode.OK)).GetProperty("key").GetString());
        }

        var big = (await Api.CreateItemAsync(_admin, _workspace, _bills, new { title = "Big", amount = 500 })).Id();
        var small = (await Api.CreateItemAsync(_admin, _workspace, _bills, new { title = "Small", amount = 5 })).Id();
        await RunAsync(await StartAsync(source.Id(), big), "completed");
        await RunAsync(await StartAsync(source.Id(), small), "completed");
        await RunsOfAsync(onEvent, 1);
        await RunsOfAsync(onCompleted, 2);
        Assert.Equal("over 500", (await FieldsAsync(_bills, big)).GetProperty("state").GetString());
        Assert.False((await FieldsAsync(_bills, small)).TryGetProperty("state", out _));
        Assert.Equal(["checked Big", "checked Small"], await TitlesAsync(_tasks));

        // event.raise checks its event name.
        using var reserved = await _admin.PostAsJsonAsync(Workflows, new { name = "Reserved", definition = JsonNode.Parse("""
            { "trigger": { "type": "manual" }, "flow": { "start": "r", "nodes": { "r": { "activity": "event.raise", "inputs": { "event": "completed" } } } } }
            """) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
    }

    [Fact]
    public async Task Restored_items_trigger_workflows_and_old_runs_are_cleaned_up()
    {
        var workflow = (await CreateWorkflowAsync("Restored", """
            { "trigger": { "type": "itemRestored", "list": "Bills" }, "flow": { "start": "mark", "nodes": {
              "mark": { "activity": "item.update", "inputs": { "fields": { "state": "restored" } } } } } }
            """)).Id();
        var bill = (await Api.CreateItemAsync(_admin, _workspace, _bills, new { title = "Gone" })).Id();
        var etag = (await _admin.GetAsync($"{Api.Items(_workspace, _bills)}/{bill}", Ct)).Headers.ETag!.Tag;
        using (var deleted = await _admin.SendAsync(Api.WithETag(HttpMethod.Delete, $"{Api.Items(_workspace, _bills)}/{bill}", null, etag), Ct))
        {
            Assert.True(deleted.IsSuccessStatusCode);
        }

        using (var restored = await _admin.PostAsync($"/v1.0/workspaces/{_workspace}/lists/{_bills}/recycleBin/{bill}/restore", null, Ct))
        {
            Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync(Ct));
        }

        var run = Assert.Single(await RunsOfAsync(workflow, 1));
        Assert.Equal("restored", (await FieldsAsync(_bills, bill)).GetProperty("state").GetString());

        // Finished runs older than the retention are deleted by the daily job.
        var runId = Guid.Parse(run.Id());
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var stored = await db.WorkflowRuns.SingleAsync(r => r.TenantId == _tenant && r.Id == runId, Ct);
            stored.CompletedAtUnixMs = DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds();
            await db.SaveChangesAsync(Ct);
            await ActivatorUtilities.CreateInstance<WorkflowRunCleanupJob>(scope.ServiceProvider).RunAsync(_tenant, Ct);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"{Workflows}/runs/{runId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Concurrency_per_item_skips_or_replaces_runs()
    {
        const string definition = """
            { "trigger": { "type": "manual" }, "concurrency": "{0}", "flow": { "start": "wait", "nodes": {
              "wait": { "activity": "delay", "inputs": { "hours": 1 } } } } }
            """;
        var skip = (await CreateWorkflowAsync("Skip", definition.Replace("{0}", "skip", StringComparison.Ordinal))).Id();
        var replace = (await CreateWorkflowAsync("Replace", definition.Replace("{0}", "replace", StringComparison.Ordinal))).Id();
        var bill = (await Api.CreateItemAsync(_admin, _workspace, _bills, new { title = "Busy" })).Id();

        var first = await StartAsync(skip, bill);
        await RunAsync(first, "waiting");
        var second = await StartAsync(skip, bill);
        await RunAsync(second, "cancelled");
        Assert.Equal("waiting", (await RunAsync(first, "waiting")).GetProperty("status").GetString());

        var older = await StartAsync(replace, bill);
        await RunAsync(older, "waiting");
        var newer = await StartAsync(replace, bill);
        await RunAsync(newer, "waiting");
        await RunAsync(older, "cancelled");

        using var invalid = await _admin.PostAsJsonAsync(Workflows, new { name = "Invalid", definition = JsonNode.Parse(definition.Replace("{0}", "queue", StringComparison.Ordinal)) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
