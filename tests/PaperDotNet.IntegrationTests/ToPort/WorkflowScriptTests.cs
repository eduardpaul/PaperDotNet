using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// Script steps and the flow fixes of ADR-0037: JavaScript that reads lists and plans writes, explicit item targets,
/// long loops, loops in loops and concurrency per item.
/// </summary>
public sealed class WorkflowScriptTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(HttpClient Admin, Guid Workspace, Guid Orders, Guid Lines)
    {
        public string Workflows => $"/v1.0/workspaces/{Workspace}/workflows";

        public string Order(Guid id) => $"/v1.0/workspaces/{Workspace}/lists/{Orders}/items/{id}";
    }

    private async Task<Setup> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var admin = await ApiClient.CreateAsync(factory, tenant);
        var ws = await admin.CreateWorkspaceAsync("Shop");
        var order = await admin.CreateContentTypeAsync("Order", [
            new { name = "amount", type = "number" },
            new { name = "status", type = "text" },
            new { name = "note", type = "text" },
            new { name = "today", type = "text" },
        ]);
        var orders = await admin.CreateListAsync(ws, "Orders", order);
        var line = await admin.CreateContentTypeAsync("Order line", [
            new { name = "order", type = "lookup", lookupListId = orders },
            new { name = "qty", type = "number" },
            new { name = "price", type = "number" },
        ]);
        return new Setup(admin, ws, orders, await admin.CreateListAsync(ws, "Lines", line));
    }

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.ReadJsonAsync();
    }

    private static async Task<string> InvalidAsync(Setup s, object flow)
    {
        var response = await s.Admin.PostAsJsonAsync(s.Workflows, new { name = "Invalid", trigger = new { type = "manual", list = "Orders" }, flow }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>Starts a manual workflow on an order and waits until the run has finished.</summary>
    private static async Task<JsonElement> RunAsync(Setup s, string workflow, Guid order)
    {
        var run = await PostIdAsync(s.Admin, $"{s.Order(order)}/workflows", new { workflow });
        JsonElement done = default;
        await Eventually.WaitForAsync(async () =>
        {
            done = await GetAsync(s.Admin, $"{s.Workflows}/runs/{run}");
            return done.GetProperty("status").GetString() is "completed" or "failed" ? true : (bool?)null;
        }, TimeSpan.FromSeconds(60));
        return done;
    }

    private static Task<Guid> ManualAsync(Setup s, string name, object flow, object? variables = null) =>
        PostIdAsync(s.Admin, s.Workflows, new { name, trigger = new { type = "manual", list = "Orders" }, variables, flow });

    private static object Script(string code) => new { start = "run", nodes = new { run = new { activity = "script", inputs = new { code } } } };

    private static async Task<List<JsonElement>> LinesAsync(Setup s) =>
        (await GetAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists/{s.Lines}/items?$top=100")).GetProperty("value").EnumerateArray().ToList();

    [Fact]
    public async Task Scripts_read_lists_and_apply_their_planned_writes()
    {
        var s = await SetupAsync("wf-script");
        var order = (await s.Admin.CreateItemAsync(s.Workspace, s.Orders, new { fields = new { title = "Order 1", amount = 1 } })).GetProperty("id").GetGuid();

        // Reads see the lists as they are; writes are planned and applied after the script, and a created item's id can
        // be used by later writes and in the result.
        await ManualAsync(s, "Price", new
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

        var first = await RunAsync(s, "Price", order);
        Assert.True(first.GetProperty("status").GetString() == "completed", first.ToString());
        var output = first.GetProperty("outputs").GetProperty("price");
        Assert.Equal(3, output.GetProperty("created").GetArrayLength());
        Assert.Equal((1, 0), (output.GetProperty("updated").GetInt32(), output.GetProperty("deleted").GetInt32()));
        Assert.Equal("undefined", output.GetProperty("result").GetProperty("dotnet").GetString()); // no .NET in scripts
        Assert.Equal(0, output.GetProperty("result").GetProperty("lists").GetInt32()); // reads do not see planned writes
        Assert.Equal(1, first.GetProperty("variables").GetProperty("count").GetInt32());
        Assert.Contains(first.GetProperty("log").EnumerateArray(), l => l.GetProperty("message").GetString() == "price: priced 3 lines of Order 1");
        var fields = (await GetAsync(s.Admin, s.Order(order))).GetProperty("fields");
        Assert.Equal(66m, Math.Round(fields.GetProperty("amount").GetDecimal(), 2));
        Assert.Equal("priced", fields.GetProperty("status").GetString());
        var firstId = output.GetProperty("created")[0].GetString();
        Assert.Equal($"first {firstId}", fields.GetProperty("note").GetString());
        var lines = await LinesAsync(s);
        Assert.Equal(["L1", "L2", "L3"], lines.Select(l => l.GetProperty("fields").GetProperty("title").GetString()).Order(StringComparer.Ordinal));
        Assert.All(lines, l => Assert.Equal(order.ToString(), l.GetProperty("fields").GetProperty("order").GetString()));

        // Running again replaces the lines: the script's writes are ordinary item changes.
        var second = await RunAsync(s, "Price", order);
        Assert.Equal(3, second.GetProperty("outputs").GetProperty("price").GetProperty("deleted").GetInt32());
        var again = await LinesAsync(s);
        Assert.Equal(3, again.Count);
        Assert.Empty(again.Select(l => l.GetProperty("id").GetString()).Intersect(lines.Select(l => l.GetProperty("id").GetString())));
    }

    [Fact]
    public async Task Scripts_are_checked_limited_and_their_failures_take_the_error_port()
    {
        var s = await SetupAsync("wf-script-limits");
        var order = (await s.Admin.CreateItemAsync(s.Workspace, s.Orders, new { fields = new { title = "Order" } })).GetProperty("id").GetGuid();
        Assert.Contains("code", await InvalidAsync(s, Script("return {")), StringComparison.Ordinal);
        Assert.Contains("code is required", await InvalidAsync(s, new { start = "run", nodes = new { run = new { activity = "script" } } }), StringComparison.Ordinal);

        async Task<string> ErrorAsync(string name, string code)
        {
            await ManualAsync(s, name, Script(code));
            var run = await RunAsync(s, name, order);
            Assert.Equal("failed", run.GetProperty("status").GetString());
            return run.GetProperty("error").GetString()!;
        }

        Assert.Contains("The script ran", await ErrorAsync("Forever", "while (true) {}"), StringComparison.Ordinal);
        Assert.Contains("The list 'Nope' does not exist in the workspace. (line 2)", await ErrorAsync("Unknown list", "const a = 1;\nawait items.query('Nope');"), StringComparison.Ordinal);
        Assert.Contains("The list 'Nope' does not exist", await ErrorAsync("Not awaited", "items.get('Nope', 'x').catch(() => null);\nreturn 1;"), StringComparison.Ordinal);
        Assert.Contains("write 1 (create in Lines)", await ErrorAsync("Bad value", "await items.create('Lines', { title: 'x', qty: 'many' });"), StringComparison.Ordinal);

        // Another workspace's lists are out of reach.
        var other = await s.Admin.CreateWorkspaceAsync("Other");
        await s.Admin.CreateListAsync(other, "Secret", await s.Admin.CreateContentTypeAsync("Secret thing", [new { name = "code", type = "text" }]));
        Assert.Contains("'Secret' does not exist", await ErrorAsync("Other workspace", "await items.query('Secret');"), StringComparison.Ordinal);

        // A script error continues on the error port, with the error as the node's output.
        await ManualAsync(s, "Caught", new
        {
            start = "run",
            nodes = new Dictionary<string, object>
            {
                ["run"] = new { activity = "script", inputs = new { code = "throw new Error('nope');" }, next = new { error = "report" } },
                ["report"] = new { activity = "item.update", inputs = new { fields = new { note = "{step:run.error}" } } },
            },
        });
        Assert.Equal("completed", (await RunAsync(s, "Caught", order)).GetProperty("status").GetString());
        Assert.Equal("nope (line 1)", (await GetAsync(s.Admin, s.Order(order))).GetProperty("fields").GetProperty("note").GetString());
    }

    [Fact]
    public async Task Items_are_addressed_explicitly_and_loops_are_safe()
    {
        var s = await SetupAsync("wf-flow-fixes");
        var order = (await s.Admin.CreateItemAsync(s.Workspace, s.Orders, new { fields = new { title = "Order", today = "field value" } })).GetProperty("id").GetGuid();
        var other = (await s.Admin.CreateItemAsync(s.Workspace, s.Orders, new { fields = new { title = "Other" } })).GetProperty("id").GetGuid();

        // item.get and items.query read any item of the workspace; item.update changes another item with list and id;
        // {item:today} is the field, {today} the date.
        await ManualAsync(s, "Explicit", new
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
        Assert.Equal("completed", (await RunAsync(s, "Explicit", order)).GetProperty("status").GetString());
        var changed = (await GetAsync(s.Admin, s.Order(other))).GetProperty("fields");
        Assert.Equal("from Order: field value (1)", changed.GetProperty("note").GetString());
        Assert.Equal(1m, changed.GetProperty("amount").GetDecimal());
        Assert.Contains("list and id go together", await InvalidAsync(s, new
        {
            start = "u",
            nodes = new { u = new { activity = "item.update", inputs = new { list = "Orders", fields = new { note = "x" } } } },
        }), StringComparison.Ordinal);

        // A long loop pauses and continues instead of failing after 500 nodes in one execution.
        await ManualAsync(s, "Long", new
        {
            start = "each",
            nodes = new Dictionary<string, object>
            {
                ["each"] = new { activity = "forEach", inputs = new { items = "{var:numbers}", @as = "n" }, next = new { item = "keep" } },
                ["keep"] = new { activity = "setVariable", inputs = new { name = "last", value = "{var:n}" }, next = new { done = "each" } },
            },
        }, new { numbers = Enumerable.Range(1, 300).ToArray() });
        var loop = await RunAsync(s, "Long", order);
        Assert.True(loop.GetProperty("status").GetString() == "completed", loop.ToString());
        Assert.Equal("300", loop.GetProperty("variables").GetProperty("last").GetString());

        // A loop inside a loop starts again for each outer element, even when a pass left it early.
        await ManualAsync(s, "Nested", new
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
        Assert.Equal("completed", (await RunAsync(s, "Nested", order)).GetProperty("status").GetString());
        Assert.Equal(["a1", "b1"], (await LinesAsync(s)).Select(l => l.GetProperty("fields").GetProperty("title").GetString()).Order(StringComparer.Ordinal));
        Assert.Contains("needs its own variable", await InvalidAsync(s, new
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
    public async Task Concurrency_per_item_skips_or_replaces_runs()
    {
        var s = await SetupAsync("wf-concurrency");
        var order = (await s.Admin.CreateItemAsync(s.Workspace, s.Orders, new { fields = new { title = "Order" } })).GetProperty("id").GetGuid();
        Assert.Contains("concurrency must be", (await (await s.Admin.PostAsJsonAsync(s.Workflows, new
        {
            name = "Bad",
            trigger = new { type = "manual", list = "Orders" },
            concurrency = "queue",
            steps = new[] { new { type = "delay", hours = 1 } },
        }, Ct)).Content.ReadAsStringAsync(Ct)), StringComparison.Ordinal);

        var ids = new Dictionary<string, Guid>();
        foreach (var mode in new[] { "skip", "replace" })
        {
            ids[mode] = await PostIdAsync(s.Admin, s.Workflows, new
            {
                name = $"Wait ({mode})",
                trigger = new { type = "manual", list = "Orders" },
                concurrency = mode,
                steps = new[] { new { type = "delay", hours = 1 } },
            });
        }

        async Task<List<JsonElement>> RunsAsync(Guid workflow) =>
            (await GetAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}&itemId={order}")).GetProperty("value").EnumerateArray().ToList();

        foreach (var mode in ids.Keys)
        {
            for (var start = 0; start < 2; start++)
            {
                var response = await s.Admin.PostAsJsonAsync($"{s.Order(order)}/workflows", new { workflow = $"Wait ({mode})" }, Ct);
                if (mode == "skip" && start == 1)
                {
                    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); // a person is told why nothing started
                    continue;
                }

                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
                var id = (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
                await Eventually.WaitForAsync(async () =>
                    (await GetAsync(s.Admin, $"{s.Workflows}/runs/{id}")).GetProperty("status").GetString() == "waiting" ? true : (bool?)null, TimeSpan.FromSeconds(30));
            }
        }

        // skip: the second start did nothing; replace: the first run was cancelled and the second waits.
        Assert.Equal(["waiting"], (await RunsAsync(ids["skip"])).Select(r => r.GetProperty("status").GetString()));
        Assert.Equal(["cancelled", "waiting"], (await RunsAsync(ids["replace"])).Select(r => r.GetProperty("status").GetString()).Order(StringComparer.Ordinal));
    }
}
