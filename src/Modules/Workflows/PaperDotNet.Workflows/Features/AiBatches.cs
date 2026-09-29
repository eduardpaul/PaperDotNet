using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaperDotNet.Abstractions;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>Batch execution of AI activities (AI-08, ADR-0036; <c>AI:Batch</c> section).</summary>
public sealed class AiBatchOptions
{
    public const string Section = "AI:Batch";

    /// <summary>How AI activities without an <c>execution</c> input run: <c>immediate</c> (default) or <c>batch</c>.</summary>
    public string Execution { get; set; } = "immediate";

    /// <summary>The longest a run waits for a batch answer (hours) unless the activity says otherwise.</summary>
    public double DeadlineHours { get; set; } = 48;
}

/// <summary>
/// Batched AI is built from workflow parts only (ADR-0036): a batched AI step waits on a wait of kind
/// <see cref="WaitKind"/> whose data is its question; the workspace's batch workflow (built-in "AI batch": a schedule
/// trigger and the <see cref="AiBatchActivity"/>) answers the waiting questions and completes the waits; the steps run
/// again with the answer.
/// </summary>
internal static class AiBatch
{
    /// <summary>A question waiting for the batch; key: the step's execution id; data: see <see cref="Data"/>.</summary>
    public const string WaitKind = "ai.batch";

    /// <summary>The batch step waiting for the provider's batches; data: <c>{ "batches": [{ "tag", "model", "providerId" }] }</c>.</summary>
    public const string PollKind = "ai.batch.poll";

    public const string MissedDeadline = "The batch did not answer by the deadline.";

    /// <summary>The wait data's field naming the batch run that took the question.</summary>
    public const string ClaimedBy = "batchRun";

    public static JsonObject Data(string hash, string model, AiQuestion question) => new()
    {
        ["hash"] = hash,
        ["model"] = model,
        ["activity"] = question.Activity,
        ["instructions"] = question.Instructions,
        ["input"] = question.Input,
        ["schema"] = question.Schema?.DeepClone(),
    };

    public static AiQuestion Question(JsonObject data) =>
        new(Text(data, "activity") ?? "ai.prompt", Text(data, "instructions") ?? string.Empty, Text(data, "input") ?? string.Empty, data["schema"] as JsonObject);

    public static string? Text(JsonObject data, string name) => data[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// Whether an enabled workflow of the workspace answers batched questions (its definition uses <c>ai.batch</c>); without
    /// one, batched steps ask at once instead of waiting for nothing.
    /// </summary>
    public static Task<bool> IsScheduledAsync(WorkflowsDbContext db, Guid workspaceId, CancellationToken ct) =>
        db.Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Enabled
            && db.Versions.Any(v => v.WorkflowId == w.Id && v.Number == w.CurrentVersion && v.Definition.Contains("\"ai.batch\"")), ct);
}

/// <summary>
/// <c>ai.batch</c> (AI-08): answers the AI questions waiting in the workspace (steps with <c>execution: batch</c>).
/// With a provider's batch API (<see cref="IAiBatchClient"/>) it sends them, one batch per model and each question once,
/// and polls until the results are in; without one, it asks the chat model (within the day's budget). Each answer
/// completes the waits of all steps that asked it. Safe to repeat: questions are taken by the batch run (the wait's data)
/// and a batch is tagged with an id derived from the step's execution id, so a retry adopts what it already sent.
/// </summary>
internal sealed partial class AiBatchActivity(
    ITenantScopeFactory scopes, ITenantContext tenant, TimeProvider time, ILogger<AiBatchActivity> logger, IAiBatchClient? client = null)
    : IWorkflowActivity
{
    private const int Page = 500;

    public string Key => "ai.batch";

    public string Description => "Answers the AI questions waiting in the workspace (steps with execution: batch): { \"pollMinutes\": 5 }.";

    public JsonObject? InputSchema => Schemas.Object([],
        ("maxQuestions", Schemas.Number("Most waiting steps taken per run (default 2000; the rest wait for the next).")),
        ("pollMinutes", Schemas.Number("How often the provider's batches are checked (default 5).")));

    public JsonObject? OutputSchema => Schemas.Object([],
        ("answered", Schemas.Number("Steps answered.")), ("failed", Schemas.Number("Steps whose question failed.")),
        ("released", Schemas.Number("Steps left for the next batch.")));

    public IEnumerable<string> Validate(JsonObject inputs)
    {
        if (inputs["maxQuestions"] is not null && Inputs.Number(inputs, "maxQuestions") is not (>= 1 and <= 100_000))
        {
            yield return "maxQuestions must be from 1 to 100000.";
        }

        if (inputs["pollMinutes"] is not null && Inputs.Number(inputs, "pollMinutes") is not (>= 1 and <= 1440))
        {
            yield return "pollMinutes must be from 1 to 1440.";
        }
    }

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.RunId is not { } runId)
        {
            return WorkflowActivityResult.Fail("ai.batch runs in a workflow run.");
        }

        // Its own scope: answers are saved (with the messages that resume the steps) apart from the batch run's progress.
        await using var scope = scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!);
        var work = new BatchWork(scope.ServiceProvider, context, runId, time);
        if (context.Resumed is { Kind: AiBatch.PollKind } poll)
        {
            return await ProviderAsync(() => CollectAsync(work, poll.Data ?? [], cancellationToken));
        }

        var max = (int)(Inputs.Number(context.Inputs, "maxQuestions") ?? 2000);
        if (client is not null)
        {
            max = Math.Min(max, client.MaxLines);
        }

        var waiting = await work.TakeAsync(max, cancellationToken);
        if (waiting.Count == 0)
        {
            return WorkflowActivityResult.Ok(Counts(0, 0, 0));
        }

        if (await work.Gateway.BudgetProblemAsync(cancellationToken) is { } problem)
        {
            LogSkipped(problem);
            return WorkflowActivityResult.Ok(Counts(0, 0, await work.ReleaseAsync(waiting, cancellationToken)));
        }

        return client is null ? await AnswerAsync(work, waiting, cancellationToken) : await ProviderAsync(() => SendAsync(work, waiting, cancellationToken));
    }

    /// <summary>A failing batch API fails the step (its retry policy runs it again, adopting what it sent), not the run's handler.</summary>
    private static async Task<WorkflowActivityResult> ProviderAsync(Func<Task<WorkflowActivityResult>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DbUpdateException))
        {
            return WorkflowActivityResult.Fail($"The AI batch API failed: {ex.Message}");
        }
    }

    /// <summary>Without a batch API: asks the chat model each question once and completes its steps' waits.</summary>
    private static async Task<WorkflowActivityResult> AnswerAsync(BatchWork work, List<WorkflowBookmark> waiting, CancellationToken ct)
    {
        int answered = 0, failed = 0;
        var groups = waiting.GroupBy(b => AiBatch.Text(BatchWork.DataOf(b), "hash")).ToList();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i].ToList();
            var data = BatchWork.DataOf(group[0]);
            var hash = groups[i].Key ?? string.Empty;
            var text = await work.Gateway.CachedAsync(hash, work.Now, ct);
            string? error = null;
            if (text is null)
            {
                var (call, problem) = await work.Gateway.CallAsync(AiBatch.Question(data), AiBatch.Text(data, "model") ?? "default", hash, work.Context.Source, work.RunId, ct);
                if (call is null)
                {
                    // The day's budget is used up: the rest wait for the next batch (or their deadline).
                    var released = await work.ReleaseAsync([.. groups.Skip(i).SelectMany(g => g)], ct);
                    return WorkflowActivityResult.Ok(Counts(answered, failed, released));
                }

                await work.Db.SaveChangesAsync(ct);
                (text, error) = (call.Response, call.Response is null ? problem : null);
            }

            var count = await work.CompleteAsync(group, text, error, ct);
            if (text is null)
            {
                failed += count;
            }
            else
            {
                answered += count;
            }
        }

        return WorkflowActivityResult.Ok(Counts(answered, failed, 0));
    }

    /// <summary>With a batch API: one batch per model (each question once), then waits and polls for the results.</summary>
    private async Task<WorkflowActivityResult> SendAsync(BatchWork work, List<WorkflowBookmark> waiting, CancellationToken ct)
    {
        var batches = new JsonArray();
        foreach (var model in waiting.GroupBy(b => AiBatch.Text(BatchWork.DataOf(b), "model") ?? "default"))
        {
            var lines = model.GroupBy(b => AiBatch.Text(BatchWork.DataOf(b), "hash") ?? string.Empty).Select(g =>
            {
                var question = AiBatch.Question(BatchWork.DataOf(g.First()));
                return new AiBatchLine(g.Key, model.Key, question.Instructions, question.Input, question.Schema);
            }).ToList();

            // The same tag when the step runs again: a batch sent before a crash or failure is adopted, not sent twice.
            var tag = Tag(work.Context.ExecutionId, model.Key);
            var providerId = await client!.FindAsync(tag, ct) ?? await client.SubmitAsync(tag, lines, ct);
            LogSubmitted(tag, providerId, lines.Count);
            batches.Add(new JsonObject { ["tag"] = tag.ToString(), ["model"] = model.Key, ["providerId"] = providerId });
        }

        return Poll(work, batches);
    }

    /// <summary>Checks the provider's batches: finished ones complete their steps' waits; the others are polled again.</summary>
    private async Task<WorkflowActivityResult> CollectAsync(BatchWork work, JsonObject state, CancellationToken ct)
    {
        var pending = new JsonArray();
        int answered = 0, failed = 0, released = 0;
        var taken = await work.TakenAsync(ct);
        foreach (var batch in (state["batches"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var providerId = AiBatch.Text(batch, "providerId")!;
            var model = AiBatch.Text(batch, "model");
            var status = await client!.GetAsync(providerId, ct);
            if (status.State == AiBatchState.Running)
            {
                pending.Add(batch.DeepClone());
                continue;
            }

            var results = status.Results.GroupBy(r => r.CustomId).ToDictionary(g => g.Key, g => g.First());
            var unanswered = new List<WorkflowBookmark>();
            foreach (var group in taken.Where(b => AiBatch.Text(BatchWork.DataOf(b), "model") == model).GroupBy(b => AiBatch.Text(BatchWork.DataOf(b), "hash") ?? string.Empty))
            {
                if (!results.TryGetValue(group.Key, out var result))
                {
                    unanswered.AddRange(group);
                    continue;
                }

                var data = BatchWork.DataOf(group.First());
                var call = AiGateway.NewCall(AiBatch.Text(data, "activity") ?? "ai.prompt", work.Context.Source, work.RunId, model ?? "default", group.Key,
                    result.Error is null ? result.Text : null, false, work.Now);
                call.InputTokens = result.InputTokens;
                call.OutputTokens = result.OutputTokens;
                call.Error = result.Error is null ? null : AiGateway.Truncate(result.Error, 2000);
                work.Db.AiCalls.Add(call);
                await work.Db.SaveChangesAsync(ct);
                var count = await work.CompleteAsync([.. group], call.Response, call.Response is null ? result.Error ?? "The model gave no answer." : null, ct);
                if (call.Response is null)
                {
                    failed += count;
                }
                else
                {
                    answered += count;
                }
            }

            // Questions the batch did not answer (it failed or expired) wait for the next batch, until their deadline.
            released += await work.ReleaseAsync(unanswered, ct);
            var ended = status.State;
            LogCollected(providerId, ended, results.Count);
        }

        return pending.Count > 0 ? Poll(work, pending) : WorkflowActivityResult.Ok(Counts(answered, failed, released));
    }

    private static WorkflowActivityResult Poll(BatchWork work, JsonArray batches)
    {
        var minutes = Inputs.Number(work.Context.Inputs, "pollMinutes") ?? 5;
        return WorkflowActivityResult.WaitAndRunAgain(AiBatch.PollKind, work.Context.ExecutionId.ToString("N"), work.Now.AddMinutes(minutes),
            new JsonObject { ["batches"] = batches });
    }

    private static JsonObject Counts(int answered, int failed, int released) =>
        new() { ["answered"] = answered, ["failed"] = failed, ["released"] = released };

    /// <summary>The batch's tag: derived from the step's execution and the model, so the same when the step runs again.</summary>
    private static Guid Tag(Guid executionId, string model) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{executionId:N}:{model}"))[..16]);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI batch skipped: {Reason}")]
    private partial void LogSkipped(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI batch {Tag} is {ProviderId} with {Lines} questions")]
    private partial void LogSubmitted(Guid tag, string providerId, int lines);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI batch {ProviderId} ended ({State}) with {Results} results")]
    private partial void LogCollected(string providerId, AiBatchState state, int results);

    /// <summary>The waits a batch run works on, in its own scope.</summary>
    private sealed class BatchWork(IServiceProvider services, WorkflowActivityContext context, Guid runId, TimeProvider time)
    {
        public WorkflowsDbContext Db { get; } = services.GetRequiredService<WorkflowsDbContext>();

        public AiGateway Gateway { get; } = services.GetRequiredService<AiGateway>();

        public WorkflowActivityContext Context => context;

        public Guid RunId => runId;

        public DateTimeOffset Now => time.GetUtcNow();

        private string Claim => runId.ToString("N");

        public static JsonObject DataOf(WorkflowBookmark bookmark) => (bookmark.Data is { } json ? JsonNode.Parse(json) as JsonObject : null) ?? [];

        private static string? ClaimOf(WorkflowBookmark bookmark) => AiBatch.Text(DataOf(bookmark), AiBatch.ClaimedBy);

        /// <summary>
        /// Takes up to <paramref name="max"/> open questions of the workspace, oldest first: free ones, this run's, and ones
        /// a batch run that ended left behind. Saved before anything is sent, so two batch runs never send the same.
        /// </summary>
        public async Task<List<WorkflowBookmark>> TakeAsync(int max, CancellationToken ct)
        {
            var taken = new List<WorkflowBookmark>();
            var runs = new Dictionary<string, bool>();
            for (var skip = 0; taken.Count < max; skip += Page)
            {
                var page = await Open().OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).Skip(skip).Take(Page).ToListAsync(ct);
                foreach (var bookmark in page)
                {
                    var claim = ClaimOf(bookmark);
                    if (claim is not null && claim != Claim)
                    {
                        if (!runs.TryGetValue(claim, out var active))
                        {
                            active = Guid.TryParseExact(claim, "N", out var other)
                                && await Db.Runs.AnyAsync(r => r.Id == other && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting), ct);
                            runs[claim] = active;
                        }

                        if (active)
                        {
                            continue;
                        }
                    }

                    if (claim != Claim)
                    {
                        var data = DataOf(bookmark);
                        data[AiBatch.ClaimedBy] = Claim;
                        bookmark.Data = data.ToJsonString();
                    }

                    taken.Add(bookmark);
                    if (taken.Count == max)
                    {
                        break;
                    }
                }

                if (page.Count < Page)
                {
                    break;
                }
            }

            await Db.SaveChangesAsync(ct);
            return taken;
        }

        /// <summary>The open questions this run took.</summary>
        public async Task<List<WorkflowBookmark>> TakenAsync(CancellationToken ct)
        {
            var taken = new List<WorkflowBookmark>();
            for (var skip = 0; ; skip += Page)
            {
                var page = await Open().OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).Skip(skip).Take(Page).ToListAsync(ct);
                taken.AddRange(page.Where(b => ClaimOf(b) == Claim));
                if (page.Count < Page)
                {
                    return taken;
                }
            }
        }

        /// <summary>Gives questions back for the next batch; how many.</summary>
        public async Task<int> ReleaseAsync(List<WorkflowBookmark> bookmarks, CancellationToken ct)
        {
            foreach (var bookmark in bookmarks)
            {
                if (Db.Entry(bookmark).State == EntityState.Detached)
                {
                    Db.Attach(bookmark); // completing another wait may have cleared the context
                }

                var data = DataOf(bookmark);
                data.Remove(AiBatch.ClaimedBy);
                bookmark.Data = data.ToJsonString();
            }

            await Db.SaveChangesAsync(ct);
            return bookmarks.Count;
        }

        /// <summary>Completes the steps' waits with the answer (<c>text</c>) or the <c>error</c>; how many were still open.</summary>
        public async Task<int> CompleteAsync(List<WorkflowBookmark> bookmarks, string? text, string? error, CancellationToken ct)
        {
            var completer = services.GetRequiredService<IWorkflowBookmarks>();
            var count = 0;
            foreach (var bookmark in bookmarks)
            {
                var payload = text is not null ? new JsonObject { ["text"] = text } : new JsonObject { ["error"] = error ?? "No answer." };
                if (await completer.CompleteAsync(AiBatch.WaitKind, bookmark.Key, payload, ct))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Open questions of the run's workspace.</summary>
        private IQueryable<WorkflowBookmark> Open() =>
            Db.Bookmarks.Where(b => b.Kind == AiBatch.WaitKind && b.CompletedAt == null && b.Data != null
                && Db.Runs.Any(r => r.Id == b.RunId && r.WorkspaceId == context.WorkspaceId));
    }
}
