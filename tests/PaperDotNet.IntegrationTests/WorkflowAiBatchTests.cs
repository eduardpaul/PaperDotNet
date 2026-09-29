using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// Batched AI activities (ADR-0036 slice 9c2, AI-08): questions queued for the organization's batch, shared by runs asking
/// the same, answered by the chat model in the batch window or by a provider's batch API (<see cref="FakeBatchClient"/>),
/// and what happens at the deadline.
/// </summary>
public sealed class WorkflowAiBatchTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TenantSummary Tenant, HttpClient Admin, string Workflows, Guid List, Guid First, Guid Second);

    private async Task<Setup> SetUpAsync(string name)
    {
        var tenant = await factory.CreateTenantAsync(name);
        var admin = await ApiClient.CreateAsync(factory, name);
        var ws = await admin.CreateWorkspaceAsync("Office");
        var type = await admin.CreateContentTypeAsync("Question", [new { name = "answer", type = "note" }]);
        var list = await admin.CreateListAsync(ws, "Questions", type);
        var first = (await admin.CreateItemAsync(ws, list, new { fields = new { title = "First" } })).GetProperty("id").GetGuid();
        var second = (await admin.CreateItemAsync(ws, list, new { fields = new { title = "Second" } })).GetProperty("id").GetGuid();
        return new Setup(tenant, admin, $"/v1.0/workspaces/{ws}/workflows", list, first, second);
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

    private static async Task StartAsync(Setup s, Guid workflow, params Guid[] items)
    {
        var response = await s.Admin.PostAsJsonAsync($"{s.Workflows}/{workflow}/runs", new { listId = s.List, itemIds = items }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<JsonElement> RunAsync(Setup s, Guid workflow, Guid item, params string[] statuses)
    {
        JsonElement run = default;
        await Eventually.WaitForAsync(async () =>
        {
            var response = await s.Admin.GetAsync($"{s.Workflows}/runs?workflowId={workflow}&itemId={item}", Ct);
            run = (await response.ReadJsonAsync()).GetProperty("value").EnumerateArray().FirstOrDefault();
            return run.ValueKind == JsonValueKind.Object && statuses.Contains(run.GetProperty("status").GetString()) ? true : (bool?)null;
        }, TimeSpan.FromSeconds(30));
        return run;
    }

    private static async Task<string?> AnswerAsync(Setup s, Guid item)
    {
        var workspace = s.Workflows.Split('/')[3];
        var response = await s.Admin.GetAsync($"/v1.0/workspaces/{workspace}/lists/{s.List}/items/{item}", Ct);
        return (await response.ReadJsonAsync()).GetProperty("fields").TryGetProperty("answer", out var answer) ? answer.GetString() : null;
    }

    private async Task InTenantAsync(Setup s, Func<IServiceProvider, WorkflowsDbContext, Task> action)
    {
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier);
        await action(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>());
    }

    [Fact]
    public async Task Batched_questions_wait_for_the_batch_window_and_share_one_call()
    {
        var s = await SetUpAsync("wf-batch");
        var batched = await WorkflowAsync(s, "Batched", new { prompt = "Is the office open on Sunday?", execution = "batch" });
        await StartAsync(s, batched, s.First, s.Second);
        await RunAsync(s, batched, s.First, "waiting");
        await RunAsync(s, batched, s.Second, "waiting");

        // Both runs asked the same (at the same time): one queued request with two waiters, no call yet.
        await InTenantAsync(s, async (_, db) =>
        {
            Assert.Equal(0, await db.AiCalls.CountAsync(Ct));
            var request = Assert.Single(await db.AiBatchRequests.AsNoTracking().ToListAsync(Ct));
            Assert.Equal(AiBatchRequestStatus.Queued, request.Status);
            Assert.Equal(2, request.Waiters);
            Assert.Equal("ai.prompt", request.Activity);
        });

        // The batch window (no batch API: the chat model answers each question once) lets both runs go on.
        await InTenantAsync(s, (services, _) => services.GetRequiredService<AiBatchSubmitJob>().RunAsync(Ct));
        Assert.Equal("completed", (await RunAsync(s, batched, s.First, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("completed", (await RunAsync(s, batched, s.Second, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Echo: Is the office open on Sunday?", await AnswerAsync(s, s.First));
        Assert.Equal("Echo: Is the office open on Sunday?", await AnswerAsync(s, s.Second));
        await InTenantAsync(s, async (_, db) =>
        {
            var request = await db.AiBatchRequests.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(AiBatchRequestStatus.Completed, request.Status);
            Assert.Null(request.Question);
            // One call to the model (by the batch), and each run's use of the answer.
            var call = Assert.Single(await db.AiCalls.Where(c => !c.Cached).ToListAsync(Ct));
            Assert.Null(call.RunId);
            Assert.True(call.InputTokens > 0);
            Assert.Equal(2, await db.AiCalls.CountAsync(c => c.Cached && c.RunId != null, Ct));
        });

        // At the deadline a run calls the model itself (default), or fails (onDeadline: fail).
        var late = await WorkflowAsync(s, "Late", new { prompt = "Who has the key?", execution = "batch", deadlineHours = 2 });
        var strict = await WorkflowAsync(s, "Strict", new { prompt = "Who locks up?", execution = "batch", onDeadline = "fail" });
        await StartAsync(s, late, s.First);
        await StartAsync(s, strict, s.First);
        await RunAsync(s, late, s.First, "waiting");
        await RunAsync(s, strict, s.First, "waiting");
        await InTenantAsync(s, async (services, db) =>
        {
            var past = DateTimeOffset.UtcNow.AddMinutes(-1);
            Assert.All(await db.AiBatchRequests.Where(r => r.Status == AiBatchRequestStatus.Queued).ToListAsync(Ct),
                r => Assert.True(r.DeadlineAt < DateTimeOffset.UtcNow.AddDays(3)));
            await db.AiBatchRequests.Where(r => r.Status == AiBatchRequestStatus.Queued).ExecuteUpdateAsync(u => u.SetProperty(r => r.DeadlineAt, past), Ct);
            await db.Bookmarks.Where(b => b.Kind == "ai.batch" && b.CompletedAt == null).ExecuteUpdateAsync(u => u.SetProperty(b => b.ResumeAt, past), Ct);
            await services.GetRequiredService<WorkflowTimerJob>().RunAsync(Ct);
        });
        Assert.Equal("completed", (await RunAsync(s, late, s.First, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Echo: Who has the key?", await AnswerAsync(s, s.First));
        var failed = await RunAsync(s, strict, s.First, "completed", "failed");
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("deadline", failed.GetProperty("error").GetString(), StringComparison.Ordinal);

        // Requests past their deadline are not sent any more.
        await InTenantAsync(s, async (services, db) =>
        {
            await services.GetRequiredService<AiBatchResultsJob>().RunAsync(Ct);
            Assert.Equal(0, await db.AiBatchRequests.CountAsync(r => r.Status == AiBatchRequestStatus.Queued, Ct));
        });

        // Invalid batch inputs are rejected when the workflow is saved.
        var invalid = await s.Admin.PostAsJsonAsync(s.Workflows, new
        {
            name = "Invalid",
            trigger = new { type = "manual", list = "Questions" },
            flow = new { start = "ask", nodes = new { ask = new { activity = "ai.prompt", inputs = new { prompt = "?", execution = "later" } } } },
        }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task A_batch_api_gets_the_questions_and_a_crash_after_submitting_is_recovered()
    {
        var s = await SetUpAsync("wf-batch-api");
        var client = new FakeBatchClient { CrashAfterSubmit = true };
        var provider = await WorkflowAsync(s, "Provider", new { prompt = "Summarize the week", execution = "batch" });
        await StartAsync(s, provider, s.First);
        await RunAsync(s, provider, s.First, "waiting");

        // The batch reaches the provider, but the server dies before saving its id.
        await InTenantAsync(s, async (services, db) =>
        {
            await Assert.ThrowsAsync<IOException>(() => ActivatorUtilities.CreateInstance<AiBatchSubmitJob>(services, client).RunAsync(Ct));
            var batch = await db.AiBatches.SingleAsync(Ct);
            Assert.Equal(AiBatchPhase.Preparing, batch.Status);
            Assert.Equal(AiBatchRequestStatus.Submitted, (await db.AiBatchRequests.SingleAsync(Ct)).Status);
            await db.AiBatches.ExecuteUpdateAsync(u => u.SetProperty(b => b.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-20)), Ct);

            // The results job finds it at the provider (not sent twice) and collects the answers.
            await ActivatorUtilities.CreateInstance<AiBatchResultsJob>(services, client).RunAsync(Ct);
            Assert.Single(client.Submitted);
            Assert.Equal(AiBatchPhase.Completed, (await db.AiBatches.AsNoTracking().SingleAsync(Ct)).Status);
            var call = await db.AiCalls.SingleAsync(c => c.Source.StartsWith("batch:"), Ct);
            Assert.Equal((10, 5), (call.InputTokens, call.OutputTokens));
        });
        Assert.Equal("completed", (await RunAsync(s, provider, s.First, "completed", "failed")).GetProperty("status").GetString());
        Assert.Equal("Batch: Summarize the week", await AnswerAsync(s, s.First));

        // The same question later is answered from the cache without waiting.
        await StartAsync(s, provider, s.Second);
        Assert.Equal("completed", (await RunAsync(s, provider, s.Second, "completed", "failed", "waiting")).GetProperty("status").GetString());
        Assert.Equal("Batch: Summarize the week", await AnswerAsync(s, s.Second));

        // A batch that fails at the provider queues its questions again (before their deadline).
        client.CrashAfterSubmit = false;
        client.State = AiBatchState.Failed;
        var expired = await WorkflowAsync(s, "Expired", new { prompt = "Plan next week", execution = "batch" });
        await StartAsync(s, expired, s.First);
        await RunAsync(s, expired, s.First, "waiting");
        await InTenantAsync(s, async (services, db) =>
        {
            await ActivatorUtilities.CreateInstance<AiBatchSubmitJob>(services, client).RunAsync(Ct);
            Assert.Equal(2, client.Submitted.Count);
            await ActivatorUtilities.CreateInstance<AiBatchResultsJob>(services, client).RunAsync(Ct);
            var request = await db.AiBatchRequests.AsNoTracking().OrderByDescending(r => r.CreatedAt).FirstAsync(Ct);
            Assert.Equal(AiBatchRequestStatus.Queued, request.Status);
            Assert.Null(request.BatchId);
            Assert.Contains(await db.AiBatches.AsNoTracking().ToListAsync(Ct), b => b is { Status: AiBatchPhase.Failed, Error: "expired" });
        });
        Assert.Equal("waiting", (await RunAsync(s, expired, s.First, "waiting")).GetProperty("status").GetString());
    }

    /// <summary>A provider's batch API in memory: answers <c>Batch: </c> and the input; can fail after submitting, or fail batches.</summary>
    private sealed class FakeBatchClient : IAiBatchClient
    {
        public List<(Guid BatchId, IReadOnlyList<AiBatchLine> Lines)> Submitted { get; } = [];

        public bool CrashAfterSubmit { get; set; }

        public AiBatchState State { get; set; } = AiBatchState.Completed;

        private static string ProviderId(Guid batchId) => "batch_" + batchId.ToString("N");

        public Task<string> SubmitAsync(Guid batchId, IReadOnlyList<AiBatchLine> lines, CancellationToken cancellationToken)
        {
            Submitted.Add((batchId, lines));
            return CrashAfterSubmit ? throw new IOException("The connection was lost.") : Task.FromResult(ProviderId(batchId));
        }

        public Task<string?> FindAsync(Guid batchId, CancellationToken cancellationToken) =>
            Task.FromResult(Submitted.Any(b => b.BatchId == batchId) ? ProviderId(batchId) : null);

        public Task<AiBatchStatus> GetAsync(string providerBatchId, CancellationToken cancellationToken)
        {
            var (_, lines) = Submitted.Single(b => ProviderId(b.BatchId) == providerBatchId);
            return Task.FromResult(State == AiBatchState.Completed
                ? new AiBatchStatus(AiBatchState.Completed, [.. lines.Select(l => new AiBatchResult(l.CustomId, "Batch: " + l.Input, 10, 5))])
                : new AiBatchStatus(AiBatchState.Failed, [], "expired"));
        }
    }
}
