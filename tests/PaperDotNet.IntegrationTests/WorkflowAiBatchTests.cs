using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.AiWorkflows.Data;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// Batched AI activities (ADR-0036 slice 9c2, AI-08), built from workflow parts only: batched steps wait with their
/// question as the wait's data, and the built-in "AI batch" workflow (a schedule and <c>ai.batch</c>) answers them with the
/// chat model or a provider's batch API (<see cref="FakeBatchClient"/>).
/// </summary>
public sealed class WorkflowAiBatchTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TenantSummary Tenant, HttpClient Admin, Guid Workspace, string Workflows, Guid List, Guid First, Guid Second);

    private async Task<Setup> SetUpAsync(string name)
    {
        var tenant = await factory.CreateTenantAsync(name);
        var admin = await ApiClient.CreateAsync(factory, name);
        var ws = await admin.CreateWorkspaceAsync("Office");
        var type = await admin.CreateContentTypeAsync("Question", [new { name = "answer", type = "note" }]);
        var list = await admin.CreateListAsync(ws, "Questions", type);
        var first = (await admin.CreateItemAsync(ws, list, new { fields = new { title = "First" } })).GetProperty("id").GetGuid();
        var second = (await admin.CreateItemAsync(ws, list, new { fields = new { title = "Second" } })).GetProperty("id").GetGuid();
        return new Setup(tenant, admin, ws, $"/v1.0/workspaces/{ws}/workflows", list, first, second);
    }

    /// <summary>A manual workflow that asks <paramref name="ask"/> (ai.prompt inputs) and writes the answer to the item.</summary>
    private static async Task<Guid> WorkflowAsync(Setup s, string name, object ask)
    {
        var response = await s.Admin.PostAsJsonAsync(s.Workflows, new
        {
            name,
            trigger = new { type = "manual", list = "Questions" },
            flow = new
            {
                start = "ask",
                nodes = new Dictionary<string, object>
                {
                    ["ask"] = new { activity = "ai.prompt", inputs = ask, next = new { done = "save" } },
                    ["save"] = new { activity = "item.update", inputs = new { fields = new { answer = "{step:ask.text}" } } },
                },
            },
        }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    /// <summary>Turns on the built-in "AI batch" workflow (on a schedule that does not come by itself during the test).</summary>
    private static async Task<Guid> EnableBatchAsync(Setup s)
    {
        var response = await s.Admin.PutAsJsonAsync($"{s.Workflows}/builtIns/ai.batchWindow",
            new { enabled = true, parameters = new { schedule = "0 0 1 1 *", timeZone = "UTC" } }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("workflowId").GetGuid();
    }

    private static async Task StartAsync(Setup s, Guid workflow, params Guid[] items)
    {
        var response = await s.Admin.PostAsJsonAsync($"{s.Workflows}/{workflow}/runs", new { listId = s.List, itemIds = items }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The newest run of the workflow (on the item, if given) once it has one of the statuses.</summary>
    private static async Task<JsonElement> RunAsync(Setup s, Guid workflow, Guid? item, params string[] statuses)
    {
        JsonElement run = default;
        await Eventually.WaitForAsync(async () =>
        {
            var response = await s.Admin.GetAsync($"{s.Workflows}/runs?workflowId={workflow}" + (item is null ? string.Empty : $"&itemId={item}"), Ct);
            run = (await response.ReadJsonAsync()).GetProperty("value").EnumerateArray().OrderByDescending(r => r.GetProperty("startedAt").GetDateTimeOffset()).FirstOrDefault();
            return run.ValueKind == JsonValueKind.Object && statuses.Contains(run.GetProperty("status").GetString()) ? true : (bool?)null;
        }, TimeSpan.FromSeconds(30));
        return run;
    }

    private static async Task<string?> AnswerAsync(Setup s, Guid item)
    {
        var response = await s.Admin.GetAsync($"/v1.0/workspaces/{s.Workspace}/lists/{s.List}/items/{item}", Ct);
        return (await response.ReadJsonAsync()).GetProperty("fields").TryGetProperty("answer", out var answer) ? answer.GetString() : null;
    }

    private async Task InTenantAsync(Setup s, Func<IServiceProvider, WorkflowsDbContext, Task> action)
    {
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier);
        await action(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>());
    }

    /// <summary>The batch window: the batch workflow's schedule comes due.</summary>
    private async Task TriggerBatchAsync(Setup s, Guid batch)
    {
        // Separate scopes: the job's context must not keep the schedule row it read before the change.
        await InTenantAsync(s, (services, _) => services.GetRequiredService<WorkflowScheduleJob>().RunAsync(Ct));
        await InTenantAsync(s, (_, db) =>
            db.Schedules.Where(x => x.Id == batch).ExecuteUpdateAsync(u => u.SetProperty(x => x.NextAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));
        await InTenantAsync(s, (services, _) => services.GetRequiredService<WorkflowScheduleJob>().RunAsync(Ct));
    }

    /// <summary>Waits of a kind come due now, and the minute job completes them.</summary>
    private Task ElapseAsync(Setup s, string kind) => InTenantAsync(s, async (services, db) =>
    {
        await db.Bookmarks.Where(b => b.Kind == kind && b.CompletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.ResumeAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
        await services.GetRequiredService<WorkflowTimerJob>().RunAsync(Ct);
    });

    [Fact]
    public async Task Batched_questions_wait_for_the_batch_workflow_and_share_one_call()
    {
        var s = await SetUpAsync("wf-batch");

        // Without a batch workflow in the workspace nothing would answer, so a batched step asks at once.
        var now = await WorkflowAsync(s, "Now", new { prompt = "Where is the printer?", execution = "batch" });
        await StartAsync(s, now, s.First);
        Assert.Equal("completed", (await RunAsync(s, now, s.First, "completed", "failed")).GetProperty("status").GetString());

        var batch = await EnableBatchAsync(s);
        var batched = await WorkflowAsync(s, "Batched", new { prompt = "Is the office open on Sunday?", execution = "batch" });
        await StartAsync(s, batched, s.First, s.Second);
        await RunAsync(s, batched, s.First, "waiting");
        await RunAsync(s, batched, s.Second, "waiting");

        // The questions are waits with the question as their data; the model was not asked yet.
        await InTenantAsync(s, async (services, db) =>
        {
            var waits = await db.Bookmarks.AsNoTracking().Where(b => b.Kind == "ai.batch").ToListAsync(Ct);
            Assert.Equal(2, waits.Count);
            Assert.All(waits, w => Assert.Contains("Is the office open on Sunday?", w.Data, StringComparison.Ordinal));
            Assert.Single(waits.Select(w => JsonNode.Parse(w.Data!)!["hash"]!.GetValue<string>()).Distinct());
            Assert.Equal(1, await services.GetRequiredService<AiWorkflowsDbContext>().AiCalls.CountAsync(c => !c.Cached, Ct)); // "Now" only
        });

        // The batch window: the batch workflow asks the chat model once and both steps go on.
        await TriggerBatchAsync(s, batch);
        var batchRun = await RunAsync(s, batch, null, "completed", "failed");
        Assert.Equal("completed", batchRun.GetProperty("status").GetString());
        Assert.Equal(2, batchRun.GetProperty("outputs").GetProperty("batch").GetProperty("answered").GetInt32());
        Assert.Equal("completed", (await RunAsync(s, batched, s.First, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("completed", (await RunAsync(s, batched, s.Second, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Echo: Is the office open on Sunday?", await AnswerAsync(s, s.First));
        Assert.Equal("Echo: Is the office open on Sunday?", await AnswerAsync(s, s.Second));
        var batchRunId = batchRun.GetProperty("id").GetGuid();
        await InTenantAsync(s, async (services, db) =>
        {
            var call = Assert.Single(await services.GetRequiredService<AiWorkflowsDbContext>().AiCalls.Where(c => !c.Cached && c.RunId == batchRunId).ToListAsync(Ct));
            Assert.True(call.InputTokens > 0);
            Assert.Equal(2, await services.GetRequiredService<AiWorkflowsDbContext>().AiCalls.CountAsync(c => c.Cached && c.Response == call.Response, Ct));
            Assert.Equal(0, await db.Bookmarks.CountAsync(b => b.Kind == "ai.batch" && b.CompletedAt == null, Ct));
        });

        // At the deadline a step asks the model itself (default), or fails (onDeadline: fail).
        var late = await WorkflowAsync(s, "Late", new { prompt = "Who has the key?", execution = "batch", deadlineHours = 2 });
        var strict = await WorkflowAsync(s, "Strict", new { prompt = "Who locks up?", execution = "batch", onDeadline = "fail" });
        await StartAsync(s, late, s.First);
        await StartAsync(s, strict, s.First);
        await RunAsync(s, late, s.First, "waiting");
        await RunAsync(s, strict, s.First, "waiting");
        await ElapseAsync(s, "ai.batch");
        Assert.Equal("completed", (await RunAsync(s, late, s.First, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Echo: Who has the key?", await AnswerAsync(s, s.First));
        var failed = await RunAsync(s, strict, s.First, "completed", "failed");
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("deadline", failed.GetProperty("error").GetString(), StringComparison.Ordinal);

        // Invalid batch inputs are rejected when the workflow is saved.
        var invalid = await s.Admin.PostAsJsonAsync(s.Workflows, new
        {
            name = "Invalid",
            trigger = new { type = "manual", list = "Questions" },
            flow = new { start = "ask", nodes = new { ask = new { activity = "ai.prompt", inputs = new { prompt = "?", execution = "later" } } } },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task A_batch_api_gets_each_question_once_and_a_failed_step_adopts_what_it_sent()
    {
        var s = await SetUpAsync("wf-batch-api");
        var client = FakeBatchClient.For(s.Tenant.Identifier)!;
        var batch = await EnableBatchAsync(s);
        var provider = await WorkflowAsync(s, "Provider", new { prompt = "Summarize the week", execution = "batch" });
        await StartAsync(s, provider, s.First);
        await RunAsync(s, provider, s.First, "waiting");

        // The batch reaches the provider, but the step fails before it knows: its retry adopts the batch (not sent twice).
        client.CrashAfterSubmit = true;
        await TriggerBatchAsync(s, batch);
        var batchRun = await RunAsync(s, batch, null, "waiting");
        Assert.Contains("connection was lost", batchRun.GetProperty("log").ToString(), StringComparison.Ordinal);
        Assert.Single(client.Submitted);
        client.CrashAfterSubmit = false;
        await ElapseAsync(s, "retry");
        await Eventually.WaitForAsync(async () =>
        {
            var found = false;
            await InTenantAsync(s, async (services, db) => found = await db.Bookmarks.AnyAsync(b => b.Kind == "ai.batch.poll" && b.CompletedAt == null, Ct));
            return found ? true : (bool?)null;
        }, TimeSpan.FromSeconds(30));
        Assert.Single(client.Submitted);

        // Provider batches take hours: polls that find it still running are not attempts without progress.
        client.State = AiBatchState.Running;
        for (var poll = 0; poll < WorkflowInterpreter.MaxAttempts + 2; poll++)
        {
            await ElapseAsync(s, "ai.batch.poll");
            await Eventually.WaitForAsync(async () =>
            {
                var polling = false;
                // The poll wait is open again once the step ran and still found the batch running.
                await InTenantAsync(s, async (_, db) => polling = await db.Bookmarks.AnyAsync(b => b.Kind == "ai.batch.poll" && b.CompletedAt == null, Ct));
                return polling ? true : (bool?)null;
            }, TimeSpan.FromSeconds(30));
        }

        Assert.Equal("waiting", (await RunAsync(s, batch, null, "waiting", "failed")).GetProperty("status").GetString());
        client.State = AiBatchState.Completed;

        // The next poll finds the results: the step goes on with the answer.
        await ElapseAsync(s, "ai.batch.poll");
        Assert.Equal("completed", (await RunAsync(s, batch, null, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("completed", (await RunAsync(s, provider, s.First, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Batch: Summarize the week", await AnswerAsync(s, s.First));
        await InTenantAsync(s, async (services, db) =>
        {
            var call = await services.GetRequiredService<AiWorkflowsDbContext>().AiCalls.SingleAsync(c => !c.Cached, Ct);
            Assert.Equal((10, 5), (call.InputTokens, call.OutputTokens));
        });

        // The same question later is answered from the cache without waiting.
        await StartAsync(s, provider, s.Second);
        Assert.Equal("completed", (await RunAsync(s, provider, s.Second, "completed", "failed", "waiting")).GetProperty("status").GetString());
        Assert.Equal("Batch: Summarize the week", await AnswerAsync(s, s.Second));

        // A batch that fails at the provider gives its questions back for the next batch.
        client.State = AiBatchState.Failed;
        var expired = await WorkflowAsync(s, "Expired", new { prompt = "Plan next week", execution = "batch" });
        await StartAsync(s, expired, s.First);
        await RunAsync(s, expired, s.First, "waiting");
        await TriggerBatchAsync(s, batch);
        await Eventually.WaitForAsync(() => client.Submitted.Count == 2 ? Task.FromResult<bool?>(true) : Task.FromResult<bool?>(null), TimeSpan.FromSeconds(30));
        await RunAsync(s, batch, null, "waiting");
        await ElapseAsync(s, "ai.batch.poll");
        var second = await RunAsync(s, batch, null, "completed", "failed");
        Assert.Equal(1, second.GetProperty("outputs").GetProperty("batch").GetProperty("released").GetInt32());
        Assert.Equal("waiting", (await RunAsync(s, expired, s.First, "waiting")).GetProperty("status").GetString());
        await InTenantAsync(s, async (services, db) =>
        {
            var wait = await db.Bookmarks.AsNoTracking().SingleAsync(b => b.Kind == "ai.batch" && b.CompletedAt == null, Ct);
            Assert.DoesNotContain("batchRun", wait.Data, StringComparison.Ordinal);
        });
    }
}
