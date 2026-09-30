using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperDotNet.Core.Tests;

public sealed class WorkflowTests : IAsyncLifetime
{
    private readonly CoreHostFactory _host = new();
    private HttpClient _client = null!;
    private string _invoices = "";
    private string _tasks = "";

    public async ValueTask InitializeAsync()
    {
        _client = await _host.SignInAsync();
        _invoices = (await Api.CreateListAsync(_client, "Invoices", new object[]
        {
            new { name = "amount", type = "number" },
            new { name = "status", type = "choice", choices = new[] { "new", "big", "small" } },
            new { name = "touched", type = "number" },
        })).Id();
        _tasks = (await Api.CreateListAsync(_client, "Tasks", new[] { new { name = "invoice", type = "text" } })).Id();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<string> CreateWorkflowAsync(string name, string definition)
    {
        using var response = await _client.PostAsJsonAsync("/v1.0/workflows", new { name, definition = JsonNode.Parse(definition) });
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    /// <summary>The workflow's runs once <paramref name="count"/> of them have finished.</summary>
    private async Task<List<JsonElement>> RunsAsync(string workflowId, int count)
    {
        List<JsonElement> runs = [];
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var page = await (await _client.GetAsync($"/v1.0/workflows/{workflowId}/runs")).JsonAsync(HttpStatusCode.OK);
            runs = [.. page.GetProperty("value").EnumerateArray()];
            if (runs.Count >= count && runs.All(r => r.GetProperty("status").GetString() != "running"))
            {
                return runs;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Expected {count} finished runs, got: {string.Join(", ", runs.Select(r => r.GetProperty("status").GetString()))}");
        return runs;
    }

    private async Task<List<JsonElement>> ItemsAsync(string listId) =>
        [.. (await (await _client.GetAsync($"/v1.0/lists/{listId}/items")).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields"))];

    [Fact]
    public async Task Item_trigger_runs_the_flow_with_condition_tokens_actions_and_a_script()
    {
        var workflow = await CreateWorkflowAsync("Classify", """
            {
              "trigger": { "type": "itemAdded", "list": "Invoices" },
              "condition": "fields/status eq 'new'",
              "variables": { "threshold": 100 },
              "flow": { "start": "check", "nodes": {
                "check": { "activity": "if", "inputs": { "left": "{amount}", "op": "gt", "right": "{var:threshold}" }, "next": { "true": "big", "false": "small" } },
                "big": { "activity": "item.update", "inputs": { "fields": { "status": "big" } }, "next": { "done": "script" } },
                "small": { "activity": "item.update", "inputs": { "fields": { "status": "small" } } },
                "script": { "activity": "script", "inputs": { "code": [
                  "const big = await items.query('Invoices', { filter: \"fields/status eq 'big'\" });",
                  "log(`big: ${big.length}`);",
                  "await items.create('Tasks', { title: 'Review ' + item.title, invoice: item.id });",
                  "vars.reviewed = item.amount;",
                  "return big.length;" ] }, "next": { "done": "create" } },
                "create": { "activity": "item.create", "inputs": { "list": "Tasks", "fields": { "title": "Pay {title} ({amount:0.00}, {var:reviewed})" } } }
              } }
            }
            """);

        await Api.CreateItemAsync(_client, _invoices, new { title = "INV-1", amount = 250, status = "new" });
        await Api.CreateItemAsync(_client, _invoices, new { title = "INV-2", amount = 50, status = "new" });
        await Api.CreateItemAsync(_client, _invoices, new { title = "INV-3", amount = 999, status = "small" });

        var runs = await RunsAsync(workflow, 2);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal("completed", r.GetProperty("status").GetString()));
        var bigRun = runs.Single(r => r.GetProperty("outputs").GetProperty("check").GetProperty("result").GetBoolean());
        Assert.Equal(1, bigRun.GetProperty("outputs").GetProperty("script").GetProperty("result").GetInt32());
        Assert.Contains(bigRun.GetProperty("log").EnumerateArray(), l => l.GetProperty("message").GetString() == "script: big: 1");

        var statuses = (await ItemsAsync(_invoices)).ToDictionary(f => f.GetProperty("title").GetString()!, f => f.GetProperty("status").GetString());
        Assert.Equal("big", statuses["INV-1"]);
        Assert.Equal("small", statuses["INV-2"]);
        Assert.Equal("small", statuses["INV-3"]);
        var tasks = (await ItemsAsync(_tasks)).Select(f => f.GetProperty("title").GetString()).Order().ToList();
        Assert.Equal(["Pay INV-1 (250.00, 250)", "Review INV-1"], tasks);
    }

    [Fact]
    public async Task Manual_runs_take_inputs_and_errors_follow_the_error_port_or_fail_the_run()
    {
        var workflow = await CreateWorkflowAsync("Manual", """
            {
              "trigger": { "type": "manual" },
              "flow": { "start": "script", "nodes": {
                "script": { "activity": "script", "inputs": { "code": "if (vars.mode === 'throw') throw new Error('bad input'); vars.doubled = vars.n * 2;" }, "next": { "done": "set", "error": "failed" } },
                "set": { "activity": "setVariable", "inputs": { "name": "label", "value": "n={var:n}" }, "next": { "done": "check" } },
                "check": { "activity": "if", "inputs": { "left": "{var:doubled}", "op": "eq", "right": "84" }, "next": { "true": "end", "false": "stop" } },
                "stop": { "activity": "fail", "inputs": { "message": "not 42: {var:n}" } },
                "failed": { "activity": "fail", "inputs": { "message": "script said: {step:script.error}" } },
                "end": { "activity": "end" }
              } }
            }
            """);

        foreach (var inputs in new object[] { new { n = 42 }, new { n = 1 }, new { mode = "throw" } })
        {
            using var started = await _client.PostAsJsonAsync($"/v1.0/workflows/{workflow}/runs", new { inputs });
            await started.JsonAsync(HttpStatusCode.Accepted);
        }

        var runs = await RunsAsync(workflow, 3);
        var byStatus = runs.Select(r => (Status: r.GetProperty("status").GetString(), Error: r.GetProperty("error").ValueKind == JsonValueKind.String ? r.GetProperty("error").GetString() : null)).ToList();
        Assert.Contains(byStatus, r => r is ("completed", null));
        Assert.Contains(byStatus, r => r is ("failed", "not 42: 1"));
        Assert.Contains(byStatus, r => r.Status == "failed" && r.Error!.StartsWith("script said: bad input (line 1)", StringComparison.Ordinal));
        var completed = runs.Single(r => r.GetProperty("status").GetString() == "completed");
        Assert.Equal("n=42", completed.GetProperty("variables").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Scripts_are_sandboxed_and_limited()
    {
        var workflow = await CreateWorkflowAsync("Sandbox", """
            {
              "trigger": { "type": "manual" },
              "flow": { "start": "a", "nodes": {
                "a": { "activity": "script", "inputs": { "code": "return [typeof System, typeof importNamespace, typeof require].join(',');" }, "next": { "done": "b" } },
                "b": { "activity": "script", "inputs": { "code": "while (true) {}" } }
              } }
            }
            """);

        using (var started = await _client.PostAsJsonAsync($"/v1.0/workflows/{workflow}/runs", new { }))
        {
            await started.JsonAsync(HttpStatusCode.Accepted);
        }

        var run = Assert.Single(await RunsAsync(workflow, 1));
        Assert.Equal("undefined,undefined,undefined", run.GetProperty("outputs").GetProperty("a").GetProperty("result").GetString());
        Assert.Equal("failed", run.GetProperty("status").GetString());
        Assert.Contains("statements", run.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Workflows_that_trigger_themselves_stop_at_the_depth_limit()
    {
        var workflow = await CreateWorkflowAsync("Loop", """
            {
              "trigger": { "type": "itemUpdated", "list": "Invoices", "changedFields": ["touched"] },
              "flow": { "start": "touch", "nodes": {
                "touch": { "activity": "script", "inputs": { "code": "await items.update('Invoices', item.id, { touched: (item.touched ?? 0) + 1 });" } }
              } }
            }
            """);
        var item = await Api.CreateItemAsync(_client, _invoices, new { title = "Loop", status = "new" });
        using (var changed = await _client.SendAsync(Api.Patch($"/v1.0/lists/{_invoices}/items/{item.Id()}", new { fields = new { touched = 0 } }, item.ETag())))
        {
            await changed.JsonAsync(HttpStatusCode.OK);
        }

        var runs = await RunsAsync(workflow, 8);
        await Task.Delay(1000);
        runs = await RunsAsync(workflow, 8);
        Assert.Equal(8, runs.Count);
        var touched = (await ItemsAsync(_invoices)).Single(f => f.GetProperty("title").GetString() == "Loop").GetProperty("touched").GetInt32();
        Assert.Equal(8, touched);
    }

    [Theory]
    [InlineData("""{ "trigger": { "type": "never" }, "flow": { "start": "a", "nodes": { "a": { "activity": "end" } } } }""", "Unknown trigger type")]
    [InlineData("""{ "trigger": { "type": "manual" }, "flow": { "start": "x", "nodes": { "a": { "activity": "end" } } } }""", "start node")]
    [InlineData("""{ "trigger": { "type": "manual" }, "flow": { "start": "a", "nodes": { "a": { "activity": "nope" } } } }""", "unknown activity")]
    [InlineData("""{ "trigger": { "type": "manual" }, "flow": { "start": "a", "nodes": { "a": { "activity": "item.update", "inputs": { "fields": {} }, "next": { "maybe": "a" } } } } }""", "no outcome 'maybe'")]
    [InlineData("""{ "trigger": { "type": "manual" }, "flow": { "start": "a", "nodes": { "a": { "activity": "script", "inputs": { "code": "let x = ;" } } } } }""", "a: code:")]
    [InlineData("""{ "trigger": { "type": "manual" }, "flow": { "start": "a", "nodes": { "a": { "activity": "item.create", "inputs": {} } } } }""", "list is required")]
    public async Task Invalid_definitions_are_rejected(string definition, string expected)
    {
        using var response = await _client.PostAsJsonAsync("/v1.0/workflows", new { name = "Bad", definition = JsonNode.Parse(definition) });
        var problem = await response.JsonAsync(HttpStatusCode.BadRequest);
        Assert.Contains(problem.GetProperty("errors").GetProperty("definition").EnumerateArray(), e => e.GetString()!.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_definition_is_a_new_version()
    {
        var workflow = await CreateWorkflowAsync("Versions", """{ "trigger": { "type": "manual" }, "flow": { "start": "a", "nodes": { "a": { "activity": "end" } } } }""");
        var current = await (await _client.GetAsync($"/v1.0/workflows/{workflow}")).JsonAsync(HttpStatusCode.OK);
        using var changed = await _client.SendAsync(Api.Patch($"/v1.0/workflows/{workflow}",
            new { definition = JsonNode.Parse("""{ "trigger": { "type": "manual" }, "flow": { "start": "b", "nodes": { "b": { "activity": "end" } } } }""") }, current.ETag()));
        var updated = await changed.JsonAsync(HttpStatusCode.OK);
        Assert.Equal(2, updated.GetProperty("version").GetInt32());
        Assert.Equal("b", updated.GetProperty("definition").GetProperty("flow").GetProperty("start").GetString());
    }

    [Fact]
    public async Task Another_tenant_neither_sees_nor_triggers_the_workflows()
    {
        var workflow = await CreateWorkflowAsync("Private", """
            { "trigger": { "type": "itemAdded" }, "flow": { "start": "a", "nodes": { "a": { "activity": "end" } } } }
            """);
        var other = await _host.CreateTenantAsync("other");

        Assert.Empty((await (await other.GetAsync("/v1.0/workflows")).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());
        foreach (var response in new[]
        {
            await other.GetAsync($"/v1.0/workflows/{workflow}"),
            await other.PostAsJsonAsync($"/v1.0/workflows/{workflow}/runs", new { }),
            await other.DeleteAsync($"/v1.0/workflows/{workflow}"),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            response.Dispose();
        }

        // An item added in the other tenant starts nothing here; one added here does.
        var otherList = (await Api.CreateListAsync(other, "Notes")).Id();
        await Api.CreateItemAsync(other, otherList, new { title = "Theirs" });
        await Api.CreateItemAsync(_client, _tasks, new { title = "Ours" });
        var run = Assert.Single(await RunsAsync(workflow, 1));
        using var hidden = await other.GetAsync($"/v1.0/workflow-runs/{run.Id()}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }
}
