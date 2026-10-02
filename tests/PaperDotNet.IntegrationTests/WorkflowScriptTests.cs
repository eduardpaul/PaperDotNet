using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// Script steps and the flow fixes of ADR-0037: JavaScript that reads lists and plans writes, explicit item targets,
/// long loops and loops in loops.
/// </summary>
public sealed class WorkflowScriptTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private string _workspace = "";
    private string _orders = "";
    private string _lines = "";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Shop");
        _orders = (await Api.CreateListAsync(_admin, _workspace, "Orders", new object[]
        {
            new { name = "amount", type = "number" },
            new { name = "status", type = "text" },
            new { name = "note", type = "text" },
            new { name = "today", type = "text" },
        })).Id();
        _lines = (await Api.CreateListAsync(_admin, _workspace, "Lines", new object[]
        {
            new { name = "order", type = "lookup", lookupListId = _orders },
            new { name = "qty", type = "number" },
            new { name = "price", type = "number" },
        })).Id();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private string Workflows => $"/v1.0/workspaces/{_workspace}/workflows";

    private string Order(string id) => $"{Api.Items(_workspace, _orders)}/{id}";

    private async Task<string> OrderAsync(object fields) => (await Api.CreateItemAsync(_admin, _workspace, _orders, fields)).Id();

    private async Task<JsonElement> GetAsync(string url) => await (await _admin.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);

    private async Task<string> ManualAsync(string name, object flow, object? variables = null)
    {
        using var response = await _admin.PostAsJsonAsync(Workflows, new { name, definition = new { trigger = new { type = "manual", list = "Orders" }, variables, flow } }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private async Task<string> InvalidAsync(object flow)
    {
        using var response = await _admin.PostAsJsonAsync(Workflows, new { name = "Invalid", definition = new { trigger = new { type = "manual", list = "Orders" }, flow } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>Starts a manual workflow on an order and waits until the run has finished.</summary>
    private async Task<JsonElement> RunAsync(string workflow, string order)
    {
        using var started = await _admin.PostAsJsonAsync($"{Workflows}/{workflow}/runs", new { listId = _orders, itemId = order }, Ct);
        var run = (await started.JsonAsync(HttpStatusCode.Accepted)).Id();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var body = await GetAsync($"{Workflows}/runs/{run}");
            if (body.GetProperty("status").GetString() is "completed" or "failed")
            {
                return body;
            }

            Assert.True(DateTime.UtcNow < deadline, body.ToString());
            await Task.Delay(100, Ct);
        }
    }

    private static object Script(string code) => new { start = "run", nodes = new { run = new { activity = "script", inputs = new { code } } } };

    private async Task<List<JsonElement>> LinesAsync() =>
        [.. (await GetAsync($"{Api.Items(_workspace, _lines)}?$top=100")).GetProperty("value").EnumerateArray()];

    [Fact]
    public async Task Scripts_read_lists_and_apply_their_planned_writes()
    {
        var order = await OrderAsync(new { title = "Order 1", amount = 1 });

        // Reads see the lists as they are; writes are planned and applied after the script, and a created item's id can
        // be used by later writes and in the result.
        var price = await ManualAsync("Price", new
        {
            start = "price",
            nodes = new Dictionary<string, object>
            {
                ["price"] = new
                {
                    activity = "script",
                    inputs = new
                    {
                        code = new[]
                        {
                            "for (const old of await items.query('Lines', { filter: `fields/order eq ${item.id}` })) await items.delete('Lines', old.id);",
                            "const created = await Promise.all([1, 2, 3].map((q, i) => items.create('Lines', { title: 'L' + (i + 1), qty: q, price: q * 10, order: item.id })));",
                            "await items.update(item.list, item.id, { amount: 60 * vars.rate, status: 'priced' });",
                            "vars.count = (vars.count || 0) + 1;",
                            "log(`priced ${created.length} lines of ${item.title}`);",
                            "return { first: created[0], dotnet: typeof System, lists: (await items.query('Lines', { filter: `fields/order eq ${item.id}` })).length };",
                        },
                    },
                    next = new { done = "note" },
                },
                ["note"] = new { activity = "item.update", inputs = new { fields = new { note = "first {step:price.result.first}" } } },
            },
        }, new { rate = 1.1 });

        var first = await RunAsync(price, order);
        Assert.True(first.GetProperty("status").GetString() == "completed", first.ToString());
        var output = first.GetProperty("outputs").GetProperty("price");
        Assert.Equal(3, output.GetProperty("created").GetArrayLength());
        Assert.Equal((1, 0), (output.GetProperty("updated").GetInt32(), output.GetProperty("deleted").GetInt32()));
        Assert.Equal("undefined", output.GetProperty("result").GetProperty("dotnet").GetString()); // no .NET in scripts
        Assert.Equal(0, output.GetProperty("result").GetProperty("lists").GetInt32()); // reads do not see planned writes
        Assert.Equal(1, first.GetProperty("variables").GetProperty("count").GetInt32());
        Assert.Contains(first.GetProperty("log").EnumerateArray(), l => l.GetProperty("message").GetString() == "price: priced 3 lines of Order 1");
        var fields = (await GetAsync(Order(order))).GetProperty("fields");
        Assert.Equal(66m, Math.Round(fields.GetProperty("amount").GetDecimal(), 2));
        Assert.Equal("priced", fields.GetProperty("status").GetString());
        var firstId = output.GetProperty("created")[0].GetString();
        Assert.Equal($"first {firstId}", fields.GetProperty("note").GetString());
        var lines = await LinesAsync();
        Assert.Equal(["L1", "L2", "L3"], lines.Select(l => l.GetProperty("fields").GetProperty("title").GetString()).Order(StringComparer.Ordinal));
        Assert.All(lines, l => Assert.Equal(order, l.GetProperty("fields").GetProperty("order").GetString()));

        // Running again replaces the lines: the script's writes are ordinary item changes.
        var second = await RunAsync(price, order);
        Assert.Equal(3, second.GetProperty("outputs").GetProperty("price").GetProperty("deleted").GetInt32());
        var again = await LinesAsync();
        Assert.Equal(3, again.Count);
        Assert.Empty(again.Select(l => l.GetProperty("id").GetString()).Intersect(lines.Select(l => l.GetProperty("id").GetString())));
    }

    [Fact]
    public async Task Scripts_are_checked_limited_and_their_failures_take_the_error_port()
    {
        var order = await OrderAsync(new { title = "Order" });
        Assert.Contains("code", await InvalidAsync(Script("return {")), StringComparison.Ordinal);
        Assert.Contains("code is required", await InvalidAsync(new { start = "run", nodes = new { run = new { activity = "script" } } }), StringComparison.Ordinal);

        async Task<string> ErrorAsync(string name, string code)
        {
            var run = await RunAsync(await ManualAsync(name, Script(code)), order);
            Assert.Equal("failed", run.GetProperty("status").GetString());
            return run.GetProperty("error").GetString()!;
        }

        Assert.Contains("The script ran", await ErrorAsync("Forever", "while (true) {}"), StringComparison.Ordinal);
        Assert.Contains("The list 'Nope' does not exist in the workspace. (line 2)", await ErrorAsync("Unknown list", "const a = 1;\nawait items.query('Nope');"), StringComparison.Ordinal);
        Assert.Contains("The list 'Nope' does not exist", await ErrorAsync("Not awaited", "items.get('Nope', 'x').catch(() => null);\nreturn 1;"), StringComparison.Ordinal);
        Assert.Contains("write 1 (create in Lines)", await ErrorAsync("Bad value", "await items.create('Lines', { title: 'x', qty: 'many' });"), StringComparison.Ordinal);

        // Another workspace's lists are out of reach.
        var other = await Api.CreateWorkspaceAsync(_admin, "Other");
        await Api.CreateListAsync(_admin, other, "Secret", new[] { new { name = "code", type = "text" } });
        Assert.Contains("'Secret' does not exist", await ErrorAsync("Other workspace", "await items.query('Secret');"), StringComparison.Ordinal);

        // A script error continues on the error port, with the error as the node's output.
        var caught = await ManualAsync("Caught", new
        {
            start = "run",
            nodes = new Dictionary<string, object>
            {
                ["run"] = new { activity = "script", inputs = new { code = "throw new Error('nope');" }, next = new { error = "report" } },
                ["report"] = new { activity = "item.update", inputs = new { fields = new { note = "{step:run.error}" } } },
            },
        });
        Assert.Equal("completed", (await RunAsync(caught, order)).GetProperty("status").GetString());
        Assert.Equal("nope (line 1)", (await GetAsync(Order(order))).GetProperty("fields").GetProperty("note").GetString());
    }

    [Fact]
    public async Task Items_are_addressed_explicitly_and_loops_are_safe()
    {
        var order = await OrderAsync(new { title = "Order", today = "field value" });
        var other = await OrderAsync(new { title = "Other" });

        // item.get and items.query read any item of the workspace; item.update changes another item with list and id;
        // {item:today} is the field, {today} the date.
        var explicitly = await ManualAsync("Explicit", new
        {
            start = "find",
            nodes = new Dictionary<string, object>
            {
                ["find"] = new { activity = "items.query", inputs = new { list = "Orders", filter = "fields/title eq 'Other'" }, next = new { done = "get" } },
                ["get"] = new { activity = "item.get", inputs = new { list = "Orders", id = "{step:find.items.0.id}" }, next = new { done = "update" } },
                ["update"] = new
                {
                    activity = "item.update",
                    inputs = new { list = "Orders", id = "{step:get.id}", fields = new { note = "from {title}: {item:today} ({step:find.count})", amount = "{step:find.count}" } },
                },
            },
        });
        Assert.Equal("completed", (await RunAsync(explicitly, order)).GetProperty("status").GetString());
        var changed = (await GetAsync(Order(other))).GetProperty("fields");
        Assert.Equal("from Order: field value (1)", changed.GetProperty("note").GetString());
        Assert.Equal(1m, changed.GetProperty("amount").GetDecimal());
        Assert.Contains("list and id go together", await InvalidAsync(new
        {
            start = "u",
            nodes = new { u = new { activity = "item.update", inputs = new { list = "Orders", fields = new { note = "x" } } } },
        }), StringComparison.Ordinal);

        // A long loop pauses and continues instead of failing after 500 nodes in one execution.
        var longLoop = await ManualAsync("Long", new
        {
            start = "each",
            nodes = new Dictionary<string, object>
            {
                ["each"] = new { activity = "forEach", inputs = new { items = "{var:numbers}", @as = "n" }, next = new { item = "keep" } },
                ["keep"] = new { activity = "setVariable", inputs = new { name = "last", value = "{var:n}" }, next = new { done = "each" } },
            },
        }, new { numbers = Enumerable.Range(1, 300).ToArray() });
        var loop = await RunAsync(longLoop, order);
        Assert.True(loop.GetProperty("status").GetString() == "completed", loop.ToString());
        Assert.Equal(300, loop.GetProperty("variables").GetProperty("last").GetInt32()); // a single token keeps its type

        // A loop inside a loop starts again for each outer element, even when a pass left it early.
        var nested = await ManualAsync("Nested", new
        {
            start = "outer",
            nodes = new Dictionary<string, object>
            {
                ["outer"] = new { activity = "forEach", inputs = new { items = new[] { "a", "b" }, @as = "o" }, next = new { item = "inner" } },
                ["inner"] = new { activity = "forEach", inputs = new { items = new[] { 1, 2, 3 }, @as = "n" }, next = new { item = "second?", done = "outer" } },
                ["second?"] = new { activity = "if", inputs = new { left = "{var:n}", op = "eq", right = "2" }, next = new { @true = "outer", @false = "add" } },
                ["add"] = new { activity = "item.create", inputs = new { list = "Lines", fields = new { title = "{var:o}{var:n}" } }, next = new { done = "inner" } },
            },
        });
        Assert.Equal("completed", (await RunAsync(nested, order)).GetProperty("status").GetString());
        Assert.Equal(["a1", "b1"], (await LinesAsync()).Select(l => l.GetProperty("fields").GetProperty("title").GetString()).Order(StringComparer.Ordinal));
        Assert.Contains("needs its own variable", await InvalidAsync(new
        {
            start = "outer",
            nodes = new Dictionary<string, object>
            {
                ["outer"] = new { activity = "forEach", inputs = new { items = new[] { 1 } }, next = new { item = "inner" } },
                ["inner"] = new { activity = "forEach", inputs = new { items = new[] { 1 } }, next = new { item = "x", done = "outer" } },
                ["x"] = new { activity = "setVariable", inputs = new { name = "v", value = 1 }, next = new { done = "inner" } },
            },
        }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_are_filed_into_folder_paths_and_deleted()
    {
        var order = await OrderAsync(new { title = "Order 7", status = "paid/closed" });
        var line = (await Api.CreateItemAsync(_admin, _workspace, _lines, new { title = "Old line" })).Id();
        var archive = await ManualAsync("Archive", new
        {
            start = "file",
            nodes = new Dictionary<string, object>
            {
                ["file"] = new { activity = "item.file", inputs = new { folder = "Archive/{status}", title = "Archived {title}" }, next = new { done = "drop" } },
                ["drop"] = new { activity = "item.delete", inputs = new { list = "Lines", id = line }, next = new { done = "again" } },
                ["again"] = new { activity = "item.delete", inputs = new { list = "Lines", id = line } },
            },
        });

        var run = await RunAsync(archive, order);
        Assert.True(run.GetProperty("status").GetString() == "completed", run.ToString());
        Assert.Equal("Archive/paid-closed", run.GetProperty("outputs").GetProperty("file").GetProperty("folder").GetString());
        var item = await GetAsync(Order(order));
        Assert.Equal("Archived Order 7", item.GetProperty("fields").GetProperty("title").GetString());
        var folder = await GetAsync($"{Api.Items(_workspace, _orders)}/{item.GetProperty("parentId").GetString()}");
        Assert.Equal("paid-closed", folder.GetProperty("fields").GetProperty("title").GetString());
        Assert.Empty(await LinesAsync());
    }
}
