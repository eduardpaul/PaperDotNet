using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.AiWorkflows.Data;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>AI activities of workflows (AI-02…04, AI-06…08) against a deterministic chat model (<see cref="ReadingChatClient"/>).</summary>
public sealed class WorkflowAiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A host with the chat model (and a provider's batch API, if given), signed in, with a workspace.</summary>
    private sealed class Setup : IAsyncDisposable
    {
        public ReadingChatClient Chat { get; } = new();

        public TestHost Host { get; private set; } = null!;

        public HttpClient Admin { get; private set; } = null!;

        public Guid Tenant { get; private set; }

        public string Workspace { get; private set; } = "";

        public string Workflows => $"/v1.0/workspaces/{Workspace}/workflows";

        public static async Task<Setup> CreateAsync(IAiBatchClientHolder? batch = null)
        {
            var setup = new Setup();
            setup.Host = new TestHost(
                services =>
                {
                    services.AddSingleton<IChatClient>(setup.Chat);
                    if (batch?.Client is { } client)
                    {
                        services.AddSingleton<PaperDotNet.Workflows.Contracts.IAiBatchClient>(client);
                    }
                },
                new Dictionary<string, string> { ["AI:Chat:DailyTokens"] = "1000000" });
            setup.Admin = await setup.Host.SignInAsync();
            setup.Tenant = Guid.Parse((await (await setup.Admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
            setup.Workspace = await Api.CreateWorkspaceAsync(setup.Admin, "Office");
            return setup;
        }

        public async Task<string> CreateWorkflowAsync(string name, string definition)
        {
            using var response = await Admin.PostAsJsonAsync(Workflows, new { name, definition = JsonNode.Parse(definition) }, Ct);
            return (await response.JsonAsync(HttpStatusCode.Created)).Id();
        }

        public async Task<string> StartAsync(string workflow, string list, string item)
        {
            using var response = await Admin.PostAsJsonAsync($"{Workflows}/{workflow}/runs", new { listId = list, itemId = item }, Ct);
            return (await response.JsonAsync(HttpStatusCode.Accepted)).Id();
        }

        public async Task<JsonElement> RunAsync(string run, params string[] statuses)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var body = await (await Admin.GetAsync($"{Workflows}/runs/{run}", Ct)).JsonAsync(HttpStatusCode.OK);
                if (statuses.Contains(body.GetProperty("status").GetString()))
                {
                    return body;
                }

                Assert.True(DateTime.UtcNow < deadline, body.ToString());
                await Task.Delay(100, Ct);
            }
        }

        /// <summary>The single run of the workflow once it has finished.</summary>
        public async Task<JsonElement> FinishedAsync(string workflow, int count = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var runs = (await (await Admin.GetAsync($"{Workflows}/{workflow}/runs", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray().ToList();
                if (runs.Count >= count && runs.All(r => r.GetProperty("status").GetString() is "completed" or "failed"))
                {
                    return runs.OrderByDescending(r => r.GetProperty("startedAt").GetDateTimeOffset()).First();
                }

                Assert.True(DateTime.UtcNow < deadline, $"{runs.Count} runs");
                await Task.Delay(100, Ct);
            }
        }

        public async Task<JsonElement> FieldsAsync(string list, string item) =>
            (await (await Admin.GetAsync($"{Api.Items(Workspace, list)}/{item}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("fields");

        public async Task<List<AiCall>> CallsAsync()
        {
            await using var scope = Host.Services.CreateAsyncScope();
            var tenant = Tenant;
            return await scope.ServiceProvider.GetRequiredService<AiWorkflowsDbContext>().AiCalls.AsNoTracking()
                .Where(c => c.TenantId == tenant).OrderBy(c => c.CreatedAtUnixMs).ToListAsync(Ct);
        }

        public async Task InWorkflowsAsync(Func<IServiceProvider, WorkflowsDbContext, Task> action)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            await action(scope.ServiceProvider, db);
            await db.SaveChangesAsync(Ct);
        }

        /// <summary>Open waits of a kind come due now, and the minute job resumes them.</summary>
        public Task ElapseAsync(string kind) => InWorkflowsAsync(async (services, db) =>
        {
            var tenant = Tenant;
            foreach (var wait in await db.Bookmarks.Where(b => b.TenantId == tenant && b.Kind == kind && b.CompletedAtUnixMs == null).ToListAsync(Ct))
            {
                wait.ResumeAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            }

            await db.SaveChangesAsync(Ct);
            await ActivatorUtilities.CreateInstance<WorkflowTimerJob>(services).RunAsync(tenant, Ct);
        });

        /// <summary>The batch window: the batch workflow's schedule comes due.</summary>
        public async Task TriggerBatchAsync(string batchWorkflow)
        {
            // The first pass records the schedule; then its next occurrence is made due and the job starts the run.
            await InWorkflowsAsync((services, _) => ActivatorUtilities.CreateInstance<WorkflowScheduleJob>(services).RunAsync(Tenant, Ct));
            var state = WorkflowScheduleJob.StateId(Guid.Parse(batchWorkflow), 0);
            await InWorkflowsAsync(async (_, db) =>
            {
                var tenant = Tenant;
                var schedule = await db.WorkflowSchedules.SingleAsync(s => s.TenantId == tenant && s.Id == state, Ct);
                schedule.NextAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
            });
            await InWorkflowsAsync((services, _) => ActivatorUtilities.CreateInstance<WorkflowScheduleJob>(services).RunAsync(Tenant, Ct));
        }

        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }

    /// <summary>Hands a provider's batch API to the host.</summary>
    private interface IAiBatchClientHolder
    {
        FakeBatchClient? Client { get; }
    }

    private sealed class BatchApi : IAiBatchClientHolder
    {
        public FakeBatchClient? Client { get; } = new();
    }

    [Fact]
    public async Task Ai_activities_extract_classify_summarize_and_prompt()
    {
        await using var s = await Setup.CreateAsync();
        var admin = s.Admin;
        async Task<string> PostIdAsync(string url, object body) =>
            (await (await admin.PostAsJsonAsync(url, body, Ct)).JsonAsync(HttpStatusCode.Created)).Id();
        var group = await PostIdAsync("/v1.0/termStore/groups", new { name = "Documents" });
        var kinds = await PostIdAsync("/v1.0/termStore/sets", new { groupId = group, name = "Kinds" });
        var receiptTerm = await PostIdAsync($"/v1.0/termStore/sets/{kinds}/terms", new { name = "Receipt" });
        await PostIdAsync($"/v1.0/termStore/sets/{kinds}/terms", new { name = "Contract" });
        var receipts = (await Api.CreateListAsync(admin, s.Workspace, "Receipts", new object[]
        {
            new { name = "text", displayName = "Text", type = "note" },
            new { name = "store", displayName = "Store", type = "text" },
            new { name = "purchaseDate", displayName = "Purchase date", type = "date" },
            new { name = "total", displayName = "Total", type = "currency", currencyCode = "EUR" },
            new { name = "payment", displayName = "Payment", type = "choice", choices = new[] { "cash", "card" } },
            new { name = "kind", displayName = "Kind", type = "managedMetadata", termSetId = kinds },
            new { name = "summary", displayName = "Summary", type = "note" },
        })).Id();
        var reviews = (await Api.CreateListAsync(admin, s.Workspace, "Reviews")).Id();

        // Extraction fills the fields the model is sure about; a missing value leads to a review item.
        var extract = await s.CreateWorkflowAsync("Read receipts", """
            {
              "trigger": { "type": "itemAdded", "list": "Receipts" },
              "flow": {
                "start": "extract",
                "nodes": {
                  "extract": { "activity": "ai.extract", "inputs": { "fields": ["store", "purchaseDate", "total", "payment"], "minConfidence": 0.8 },
                               "next": { "done": "classify", "lowConfidence": "review" } },
                  "review": { "activity": "item.create", "inputs": { "list": "Reviews", "fields": { "title": "Check {title}: {step:extract.uncertain}" } }, "next": { "done": "classify" } },
                  "classify": { "activity": "ai.classify", "inputs": { "termSet": "Documents/Kinds", "field": "kind" }, "next": { "done": "summarize" } },
                  "summarize": { "activity": "ai.summarize", "inputs": { "field": "summary", "maxWords": 20 } }
                }
              }
            }
            """);
        var full = (await Api.CreateItemAsync(admin, s.Workspace, receipts,
            new { title = "Aldi receipt", text = "A receipt\nstore: Aldi\npurchaseDate: 2026-09-28\ntotal: 12.50 EUR\npayment: card" })).Id();
        var run = await s.FinishedAsync(extract);
        Assert.True(run.GetProperty("status").GetString() == "completed", run.ToString());
        var fields = await s.FieldsAsync(receipts, full);
        Assert.Equal("Aldi", fields.GetProperty("store").GetString());
        Assert.Equal("2026-09-28", fields.GetProperty("purchaseDate").GetString());
        Assert.Equal(12.5m, fields.GetProperty("total").GetDecimal());
        Assert.Equal("card", fields.GetProperty("payment").GetString());
        Assert.Equal(receiptTerm, fields.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(fields.GetProperty("summary").GetString()));
        Assert.Equal(4, run.GetProperty("outputs").GetProperty("extract").GetProperty("applied").GetArrayLength());
        Assert.Equal("Receipt", run.GetProperty("outputs").GetProperty("classify").GetProperty("term").GetString());

        var partial = (await Api.CreateItemAsync(admin, s.Workspace, receipts, new { title = "Market note", text = "store: Market\npayment: cash" })).Id();
        Assert.Equal("completed", (await s.FinishedAsync(extract, 2)).GetProperty("status").GetString());
        var reviewTitles = (await (await admin.GetAsync(Api.Items(s.Workspace, reviews), Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
            .Select(i => i.GetProperty("fields").GetProperty("title").GetString()).ToList();
        Assert.Contains("Check Market note: purchaseDate, total", reviewTitles);
        Assert.Equal("Market", (await s.FieldsAsync(receipts, partial)).GetProperty("store").GetString());

        // Prompts: text or JSON by a schema; the same model and input reuse the earlier answer.
        var ask = await s.CreateWorkflowAsync("Ask", """
            {
              "trigger": { "type": "manual", "list": "Receipts" },
              "flow": {
                "start": "ask",
                "nodes": {
                  "ask": { "activity": "ai.prompt", "inputs": { "prompt": "Is {store} a supermarket?" }, "next": { "done": "json" } },
                  "json": { "activity": "ai.prompt", "inputs": { "prompt": "Categorize {title}",
                            "schema": { "type": "object", "properties": { "category": { "type": "string" } }, "required": ["category"] } }, "next": { "done": "note" } },
                  "note": { "activity": "item.update", "inputs": { "fields": { "summary": "{step:ask.text} / {step:json.json.category}" } } }
                }
              }
            }
            """);
        var callsBefore = s.Chat.Calls;
        var asked = await s.RunAsync(await s.StartAsync(ask, receipts, full), "completed", "failed");
        Assert.True(asked.GetProperty("status").GetString() == "completed", asked.ToString());
        Assert.Equal("Echo: Is Aldi a supermarket? / category", (await s.FieldsAsync(receipts, full)).GetProperty("summary").GetString());
        Assert.Equal(callsBefore + 2, s.Chat.Calls);

        var calls = await s.CallsAsync();
        Assert.Contains(calls, c => c.Activity == "ai.extract" && c.Model == "reading-model" && c.InputTokens > 0 && !c.Cached && c.Source == "workflow");
        Assert.All(calls, c => Assert.NotNull(c.RunId));

        // The same question again comes from the cache: no call to the model, recorded as cached.
        var again = await s.RunAsync(await s.StartAsync(ask, receipts, full), "completed", "failed");
        Assert.Equal("completed", again.GetProperty("status").GetString());
        Assert.Equal(callsBefore + 2, s.Chat.Calls);
        Assert.True(again.GetProperty("outputs").GetProperty("ask").GetProperty("cached").GetBoolean());
        Assert.Equal(2, (await s.CallsAsync()).Count(c => c.Cached));

        // Once the day's budget is used up, AI activities fail (the run can be retried later).
        await using (var scope = s.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiWorkflowsDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.AiCalls.Add(new AiCall
            {
                Id = Guid.CreateVersion7(),
                TenantId = s.Tenant,
                Activity = "ai.prompt",
                Source = "test",
                Model = "reading-model",
                InputHash = "x",
                InputTokens = 2_000_000,
                CreatedAt = now,
                CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            });
            await db.SaveChangesAsync(Ct);
        }

        await Api.CreateItemAsync(admin, s.Workspace, receipts, new { title = "Late", text = "store: Late" });
        var over = await s.FinishedAsync(extract, 3);
        Assert.Equal("failed", over.GetProperty("status").GetString());
        Assert.Contains("budget", over.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("extract", over.GetProperty("failedNode").GetString());

        // Invalid inputs are refused when the workflow is saved; the catalog describes the AI activities.
        using var invalid = await admin.PostAsJsonAsync(s.Workflows, new
        {
            name = "Invalid",
            definition = JsonNode.Parse("""{ "trigger": { "type": "manual" }, "flow": { "start": "a", "nodes": { "a": { "activity": "ai.prompt", "inputs": { "prompt": "?", "execution": "later", "includeImages": "yes" } } } } }"""),
        }, Ct);
        var problem = (await invalid.JsonAsync(HttpStatusCode.BadRequest)).ToString();
        Assert.Contains("execution", problem, StringComparison.Ordinal);
        Assert.Contains("includeImages", problem, StringComparison.Ordinal);
        var catalog = (await (await admin.GetAsync("/v1.0/workflows/activities", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToList();
        var extractEntry = catalog.Single(a => a.GetProperty("key").GetString() == "ai.extract");
        Assert.Contains("lowConfidence", extractEntry.GetProperty("outcomes").ToString(), StringComparison.Ordinal);
        Assert.Equal("object", extractEntry.GetProperty("inputSchema").GetProperty("type").GetString());
        Assert.All(new[] { "ai.classify", "ai.summarize", "ai.prompt", "ai.batch" }, key => Assert.Contains(catalog, a => a.GetProperty("key").GetString() == key));
    }

    [Fact]
    public async Task Without_a_chat_model_ai_steps_fail_clearly_and_the_batch_workflow_is_not_offered()
    {
        await using var host = new TestHost();
        var admin = await host.SignInAsync();
        var workspace = await Api.CreateWorkspaceAsync(admin, "Office");
        var notes = (await Api.CreateListAsync(admin, workspace, "Notes")).Id();
        var item = (await Api.CreateItemAsync(admin, workspace, notes, new { title = "Hello" })).Id();
        var workflows = $"/v1.0/workspaces/{workspace}/workflows";
        using var created = await admin.PostAsJsonAsync(workflows, new
        {
            name = "Ask",
            definition = JsonNode.Parse("""{ "trigger": { "type": "manual", "list": "Notes" }, "flow": { "start": "a", "nodes": { "a": { "activity": "ai.prompt", "inputs": { "prompt": "Hi" } } } } }"""),
        }, Ct);
        var workflow = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        using var started = await admin.PostAsJsonAsync($"{workflows}/{workflow}/runs", new { listId = notes, itemId = item }, Ct);
        var run = (await started.JsonAsync(HttpStatusCode.Accepted)).Id();
        JsonElement body;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            await Task.Delay(100, Ct);
            body = await (await admin.GetAsync($"{workflows}/runs/{run}", Ct)).JsonAsync(HttpStatusCode.OK);
            Assert.True(DateTime.UtcNow < deadline, body.ToString());
        }
        while (body.GetProperty("status").GetString() is not ("completed" or "failed"));

        Assert.Equal("failed", body.GetProperty("status").GetString());
        Assert.Contains("AI:Chat", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        var batch = (await (await admin.GetAsync($"{workflows}/builtIns", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
            .Single(b => b.GetProperty("key").GetString() == "ai.batchWindow");
        Assert.False(batch.GetProperty("available").GetBoolean());
    }

    /// <summary>A manual workflow that asks <paramref name="ask"/> (ai.prompt inputs) and writes the answer to the item.</summary>
    private static Task<string> AskWorkflowAsync(Setup s, string name, string ask) => s.CreateWorkflowAsync(name, $$"""
        {
          "trigger": { "type": "manual", "list": "Questions" },
          "flow": {
            "start": "ask",
            "nodes": {
              "ask": { "activity": "ai.prompt", "inputs": {{ask}}, "next": { "done": "save" } },
              "save": { "activity": "item.update", "inputs": { "fields": { "answer": "{step:ask.text}" } } }
            }
          }
        }
        """);

    /// <summary>Turns on the built-in "AI batch" workflow (on a schedule that does not come by itself during the test).</summary>
    private static async Task<string> EnableBatchAsync(Setup s)
    {
        using var response = await s.Admin.PutAsJsonAsync($"{s.Workflows}/builtIns/ai.batchWindow",
            new { enabled = true, parameters = new { schedule = "0 0 1 1 *", timeZone = "UTC" } }, Ct);
        return (await response.JsonAsync(HttpStatusCode.OK)).GetProperty("workflowId").GetString()!;
    }

    private static async Task<(string List, string First, string Second)> QuestionsAsync(Setup s)
    {
        var list = (await Api.CreateListAsync(s.Admin, s.Workspace, "Questions", new object[] { new { name = "answer", displayName = "Answer", type = "note" } })).Id();
        var first = (await Api.CreateItemAsync(s.Admin, s.Workspace, list, new { title = "First" })).Id();
        var second = (await Api.CreateItemAsync(s.Admin, s.Workspace, list, new { title = "Second" })).Id();
        return (list, first, second);
    }

    private static async Task<List<WorkflowBookmark>> OpenWaitsAsync(Setup s, string kind)
    {
        await using var scope = s.Host.Services.CreateAsyncScope();
        var tenant = s.Tenant;
        return await scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>().Bookmarks.AsNoTracking()
            .Where(b => b.TenantId == tenant && b.Kind == kind && b.CompletedAtUnixMs == null).ToListAsync(Ct);
    }

    private static async Task<JsonElement> LatestRunAsync(Setup s, string workflow, params string[] statuses)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var runs = (await (await s.Admin.GetAsync($"{s.Workflows}/{workflow}/runs", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
                .OrderByDescending(r => r.GetProperty("startedAt").GetDateTimeOffset()).ToList();
            if (runs.Count > 0 && statuses.Contains(runs[0].GetProperty("status").GetString()))
            {
                return runs[0];
            }

            Assert.True(DateTime.UtcNow < deadline, runs.Count > 0 ? runs[0].ToString() : "no runs");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Batched_questions_wait_for_the_batch_workflow_and_share_one_call()
    {
        await using var s = await Setup.CreateAsync();
        var (list, first, second) = await QuestionsAsync(s);

        // Without a batch workflow in the workspace nothing would answer, so a batched step asks at once.
        var now = await AskWorkflowAsync(s, "Now", """{ "prompt": "Where is the printer?", "execution": "batch" }""");
        Assert.Equal("completed", (await s.RunAsync(await s.StartAsync(now, list, first), "completed", "failed")).GetProperty("status").GetString());

        var batch = await EnableBatchAsync(s);
        var batched = await AskWorkflowAsync(s, "Batched", """{ "prompt": "Is the office open on Sunday?", "execution": "batch" }""");
        var firstRun = await s.StartAsync(batched, list, first);
        var secondRun = await s.StartAsync(batched, list, second);
        await s.RunAsync(firstRun, "waiting");
        await s.RunAsync(secondRun, "waiting");

        // The questions are waits with the question as their data; the model was not asked yet.
        var waits = await OpenWaitsAsync(s, "ai.batch");
        Assert.Equal(2, waits.Count);
        Assert.All(waits, w => Assert.Contains("Is the office open on Sunday?", w.Data, StringComparison.Ordinal));
        Assert.Single(waits.Select(w => JsonNode.Parse(w.Data!)!["hash"]!.GetValue<string>()).Distinct());
        Assert.Single(await s.CallsAsync(), c => !c.Cached); // "Now" only

        // The batch window: the batch workflow asks the chat model once and both steps go on.
        await s.TriggerBatchAsync(batch);
        var batchRun = await LatestRunAsync(s, batch, "completed", "failed");
        Assert.True(batchRun.GetProperty("status").GetString() == "completed", batchRun.ToString());
        Assert.Equal(2, batchRun.GetProperty("outputs").GetProperty("batch").GetProperty("answered").GetInt32());
        Assert.Equal("completed", (await s.RunAsync(firstRun, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("completed", (await s.RunAsync(secondRun, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Echo: Is the office open on Sunday?", (await s.FieldsAsync(list, first)).GetProperty("answer").GetString());
        Assert.Equal("Echo: Is the office open on Sunday?", (await s.FieldsAsync(list, second)).GetProperty("answer").GetString());
        var batchRunId = Guid.Parse(batchRun.Id());
        var calls = await s.CallsAsync();
        var call = Assert.Single(calls, c => !c.Cached && c.RunId == batchRunId);
        Assert.True(call.InputTokens > 0);
        Assert.Equal(2, calls.Count(c => c.Cached && c.Response == call.Response));
        Assert.Empty(await OpenWaitsAsync(s, "ai.batch"));

        // At the deadline a step asks the model itself (default), or fails (onDeadline: fail).
        var late = await AskWorkflowAsync(s, "Late", """{ "prompt": "Who has the key?", "execution": "batch", "deadlineHours": 2 }""");
        var strict = await AskWorkflowAsync(s, "Strict", """{ "prompt": "Who locks up?", "execution": "batch", "onDeadline": "fail" }""");
        var lateRun = await s.StartAsync(late, list, first);
        var strictRun = await s.StartAsync(strict, list, first);
        await s.RunAsync(lateRun, "waiting");
        await s.RunAsync(strictRun, "waiting");
        await s.ElapseAsync("ai.batch");
        Assert.Equal("completed", (await s.RunAsync(lateRun, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Echo: Who has the key?", (await s.FieldsAsync(list, first)).GetProperty("answer").GetString());
        var failed = await s.RunAsync(strictRun, "completed", "failed");
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("deadline", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_api_gets_each_question_once_and_a_failed_step_adopts_what_it_sent()
    {
        var api = new BatchApi();
        var client = api.Client!;
        await using var s = await Setup.CreateAsync(api);
        var (list, first, second) = await QuestionsAsync(s);
        var batch = await EnableBatchAsync(s);
        var provider = await AskWorkflowAsync(s, "Provider", """{ "prompt": "Summarize the week", "execution": "batch" }""");
        var providerRun = await s.StartAsync(provider, list, first);
        await s.RunAsync(providerRun, "waiting");

        // The batch reaches the provider, but the step fails before it knows: its retry adopts the batch (not sent twice).
        client.CrashAfterSubmit = true;
        await s.TriggerBatchAsync(batch);
        var batchRun = await LatestRunAsync(s, batch, "waiting");
        Assert.Contains("connection was lost", batchRun.GetProperty("log").ToString(), StringComparison.Ordinal);
        Assert.Single(client.Submitted);
        client.CrashAfterSubmit = false;
        await s.ElapseAsync(BookmarkKinds.Retry);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await OpenWaitsAsync(s, "ai.batch.poll")).Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }

        Assert.Single(client.Submitted);

        // Provider batches take hours: polls that find it still running wait again.
        client.State = PaperDotNet.Workflows.Contracts.AiBatchState.Running;
        for (var poll = 0; poll < 3; poll++)
        {
            await s.ElapseAsync("ai.batch.poll");
            deadline = DateTime.UtcNow.AddSeconds(30);

            // The step ran and waits for the next poll: the same wait, due again later.
            while ((await OpenWaitsAsync(s, "ai.batch.poll")) is not [{ ResumeAtUnixMs: { } next }] || next < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                Assert.True(DateTime.UtcNow < deadline);
                await Task.Delay(100, Ct);
            }
        }

        Assert.Equal("waiting", (await LatestRunAsync(s, batch, "waiting", "failed")).GetProperty("status").GetString());
        client.State = PaperDotNet.Workflows.Contracts.AiBatchState.Completed;

        // The next poll finds the results: the step goes on with the answer.
        await s.ElapseAsync("ai.batch.poll");
        Assert.Equal("completed", (await LatestRunAsync(s, batch, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("completed", (await s.RunAsync(providerRun, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Batch: Summarize the week", (await s.FieldsAsync(list, first)).GetProperty("answer").GetString());
        var call = Assert.Single(await s.CallsAsync(), c => !c.Cached);
        Assert.Equal((10, 5), (call.InputTokens, call.OutputTokens));
        Assert.Equal(0, s.Chat.Calls);

        // The same question later is answered from the cache without waiting.
        Assert.Equal("completed", (await s.RunAsync(await s.StartAsync(provider, list, second), "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Batch: Summarize the week", (await s.FieldsAsync(list, second)).GetProperty("answer").GetString());

        // A batch that fails at the provider gives its questions back for the next batch.
        client.State = PaperDotNet.Workflows.Contracts.AiBatchState.Failed;
        var expired = await AskWorkflowAsync(s, "Expired", """{ "prompt": "Plan next week", "execution": "batch" }""");
        var expiredRun = await s.StartAsync(expired, list, first);
        await s.RunAsync(expiredRun, "waiting");
        await s.TriggerBatchAsync(batch);
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (client.Submitted.Count < 2)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }

        await LatestRunAsync(s, batch, "waiting");
        await s.ElapseAsync("ai.batch.poll");
        var secondBatch = await LatestRunAsync(s, batch, "completed", "failed");
        Assert.Equal(1, secondBatch.GetProperty("outputs").GetProperty("batch").GetProperty("released").GetInt32());
        Assert.Equal("waiting", (await s.RunAsync(expiredRun, "waiting")).GetProperty("status").GetString());
        Assert.DoesNotContain("batchRun", Assert.Single(await OpenWaitsAsync(s, "ai.batch")).Data, StringComparison.Ordinal);
    }
}
