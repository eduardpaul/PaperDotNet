using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PaperDotNet.AiWorkflows.Data;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.AiWorkflows.Features;

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
    /// <summary>The activity that answers batched questions.</summary>
    public const string ActivityKey = "ai.batch";

    /// <summary>The built-in workflow that runs it on a schedule.</summary>
    public const string WorkflowKey = "ai.batchWindow";

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

    public static string? Text(JsonObject? data, string name) => data?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The built-in "AI batch" workflow: <c>ai.batch</c> on a schedule, with a retry policy.</summary>
    public static readonly BuiltInWorkflow Workflow = new(WorkflowKey, "AI batch",
        "Answers the workspace's batched AI questions (steps with execution: batch) on a schedule, through the provider's batch API when the server has one.",
        JsonNode.Parse("""
            {
              "trigger": { "type": "schedule", "cron": "{param:schedule}", "timeZone": "{param:timeZone}" },
              "flow": {
                "start": "batch",
                "nodes": {
                  "batch": { "activity": "ai.batch", "inputs": { "maxQuestions": "{param:maxQuestions}", "pollMinutes": "{param:pollMinutes}" },
                             "retry": { "attempts": 3, "delayMinutes": 10 } }
                }
              }
            }
            """)!.AsObject())
    {
        Parameters = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "schedule": { "type": "string", "default": "0 1 * * *", "description": "When to send (cron), e.g. 0 1,13 * * * for twice a day." },
                "timeZone": { "type": "string", "description": "The schedule's time zone (default: the organization's)." },
                "maxQuestions": { "type": "number", "description": "Most questions per batch run (default 2000)." },
                "pollMinutes": { "type": "number", "default": 5, "description": "How often the provider's batches are checked." }
              }
            }
            """)!.AsObject(),
        Requires = BuiltInRequirements.Ai,
    };
}

/// <summary>
/// <c>ai.batch</c> (AI-08): answers the AI questions waiting in the workspace (steps with <c>execution: batch</c>).
/// With a provider's batch API (<see cref="IAiBatchClient"/>) it sends them, one batch per model and each question once,
/// and polls until the results are in; without one, it asks the chat model (within the day's budget). Each answer
/// completes the waits of all steps that asked it. Safe to repeat: questions are taken by the batch run (the wait's data)
/// and a batch is tagged with an id derived from the step's execution id, so a retry adopts what it already sent. Built
/// on the public workflow contracts only (<see cref="IWorkflowBookmarks"/>, <see cref="IWorkflowDirectory"/>).
/// </summary>
internal sealed partial class AiBatchActivity(
    AiGateway gateway, IWorkflowBookmarks bookmarks, IWorkflowDirectory workflows, TimeProvider time, ILogger<AiBatchActivity> logger,
    IAiBatchClient? client = null)
    : IWorkflowActivity
{
    private const int Page = 500;

    public string Key => AiBatch.ActivityKey;

    public string Description => "Answers the AI questions waiting in the workspace (steps with execution: batch): { \"pollMinutes\": 5 }.";

    public JsonObject? InputSchema => ActivitySchemas.Of([],
        ("maxQuestions", ActivitySchemas.Number("Most waiting steps taken per run (default 2000; the rest wait for the next).")),
        ("pollMinutes", ActivitySchemas.Number("How often the provider's batches are checked (default 5).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([],
        ("answered", ActivitySchemas.Number("Steps answered.")), ("failed", ActivitySchemas.Number("Steps whose question failed.")),
        ("released", ActivitySchemas.Number("Steps left for the next batch.")));

    public IEnumerable<string> Validate(JsonObject inputs)
    {
        if (inputs["maxQuestions"] is not null && ActivityInputs.Number(inputs, "maxQuestions") is not (>= 1 and <= 100_000))
        {
            yield return "maxQuestions must be from 1 to 100000.";
        }

        if (inputs["pollMinutes"] is not null && ActivityInputs.Number(inputs, "pollMinutes") is not (>= 1 and <= 1440))
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

        var work = new BatchWork(bookmarks, workflows, context, runId);
        if (context.Resumed is { Kind: AiBatch.PollKind } poll)
        {
            return await ProviderAsync(() => CollectAsync(work, poll.Data ?? [], cancellationToken));
        }

        var max = (int)(ActivityInputs.Number(context.Inputs, "maxQuestions") ?? 2000);
        if (client is not null)
        {
            max = Math.Min(max, client.MaxLines);
        }

        var waiting = await work.TakeAsync(max, cancellationToken);
        if (waiting.Count == 0)
        {
            return WorkflowActivityResult.Ok(Counts(0, 0, 0));
        }

        if (await gateway.BudgetProblemAsync(cancellationToken) is { } problem)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WorkflowActivityResult.Fail($"The AI batch API failed: {ex.Message}");
        }
    }

    /// <summary>Without a batch API: asks the chat model each question once and completes its steps' waits.</summary>
    private async Task<WorkflowActivityResult> AnswerAsync(BatchWork work, List<WorkflowOpenWait> waiting, CancellationToken ct)
    {
        int answered = 0, failed = 0;
        var groups = waiting.GroupBy(w => AiBatch.Text(w.Data, "hash")).ToList();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i].ToList();
            var data = group[0].Data ?? [];
            var hash = groups[i].Key ?? string.Empty;
            var text = await gateway.CachedAsync(hash, AiBatch.Question(data), time.GetUtcNow(), ct);
            string? error = null;
            if (text is null)
            {
                var (call, problem) = await gateway.CallAsync(AiBatch.Question(data), AiBatch.Text(data, "model") ?? "default", hash, work.Context.Source, work.RunId, ct);
                if (call is null)
                {
                    // The day's budget is used up: the rest wait for the next batch (or their deadline).
                    var released = await work.ReleaseAsync([.. groups.Skip(i).SelectMany(g => g)], ct);
                    return WorkflowActivityResult.Ok(Counts(answered, failed, released));
                }

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
    private async Task<WorkflowActivityResult> SendAsync(BatchWork work, List<WorkflowOpenWait> waiting, CancellationToken ct)
    {
        var batches = new JsonArray();
        foreach (var model in waiting.GroupBy(w => AiBatch.Text(w.Data, "model") ?? "default"))
        {
            var lines = model.GroupBy(w => AiBatch.Text(w.Data, "hash") ?? string.Empty).Select(g =>
            {
                var question = AiBatch.Question(g.First().Data ?? []);
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
            var unanswered = new List<WorkflowOpenWait>();
            foreach (var group in taken.Where(w => AiBatch.Text(w.Data, "model") == model).GroupBy(w => AiBatch.Text(w.Data, "hash") ?? string.Empty))
            {
                if (!results.TryGetValue(group.Key, out var result))
                {
                    unanswered.AddRange(group);
                    continue;
                }

                var data = group.First().Data;
                var call = AiGateway.NewCall(AiBatch.Text(data, "activity") ?? "ai.prompt", work.Context.Source, work.RunId, model ?? "default", group.Key,
                    result.Error is null ? result.Text : null, false, time.GetUtcNow());
                call.InputTokens = result.InputTokens;
                call.OutputTokens = result.OutputTokens;
                call.Error = result.Error is null ? null : AiGateway.Truncate(result.Error, 2000);
                await gateway.AddAsync(call, ct);
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

    private WorkflowActivityResult Poll(BatchWork work, JsonArray batches)
    {
        var minutes = ActivityInputs.Number(work.Context.Inputs, "pollMinutes") ?? 5;
        return WorkflowActivityResult.WaitAndRunAgain(AiBatch.PollKind, work.Context.ExecutionId.ToString("N"), time.GetUtcNow().AddMinutes(minutes),
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

    /// <summary>The waiting questions a batch run works on, through the workflow contracts.</summary>
    private sealed class BatchWork(IWorkflowBookmarks bookmarks, IWorkflowDirectory workflows, WorkflowActivityContext context, Guid runId)
    {
        public WorkflowActivityContext Context => context;

        public Guid RunId => runId;

        private string Claim => runId.ToString("N");

        /// <summary>
        /// Takes up to <paramref name="max"/> open questions of the workspace, oldest first: free ones, this run's, and ones
        /// a batch run that ended left behind. Each is marked in its wait's data before anything is sent, so two batch runs
        /// never send the same.
        /// </summary>
        public async Task<List<WorkflowOpenWait>> TakeAsync(int max, CancellationToken ct)
        {
            var taken = new List<WorkflowOpenWait>();
            var active = new Dictionary<string, bool>();
            for (var skip = 0; taken.Count < max; skip += Page)
            {
                var page = await bookmarks.ListOpenAsync(AiBatch.WaitKind, context.WorkspaceId, skip, Page, ct);
                foreach (var wait in page)
                {
                    var claim = AiBatch.Text(wait.Data, AiBatch.ClaimedBy);
                    if (claim is not null && claim != Claim)
                    {
                        if (!active.TryGetValue(claim, out var busy))
                        {
                            busy = Guid.TryParseExact(claim, "N", out var other) && await workflows.IsRunActiveAsync(other, ct);
                            active[claim] = busy;
                        }

                        if (busy)
                        {
                            continue;
                        }
                    }

                    var data = wait.Data?.DeepClone().AsObject() ?? [];
                    if (claim != Claim)
                    {
                        data[AiBatch.ClaimedBy] = Claim;
                        if (!await bookmarks.SetDataAsync(AiBatch.WaitKind, wait.Key, data, ct))
                        {
                            continue; // answered, timed out or taken meanwhile
                        }
                    }

                    taken.Add(wait with { Data = data });
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

            return taken;
        }

        /// <summary>The open questions this run took.</summary>
        public async Task<List<WorkflowOpenWait>> TakenAsync(CancellationToken ct)
        {
            var taken = new List<WorkflowOpenWait>();
            for (var skip = 0; ; skip += Page)
            {
                var page = await bookmarks.ListOpenAsync(AiBatch.WaitKind, context.WorkspaceId, skip, Page, ct);
                taken.AddRange(page.Where(w => AiBatch.Text(w.Data, AiBatch.ClaimedBy) == Claim));
                if (page.Count < Page)
                {
                    return taken;
                }
            }
        }

        /// <summary>Gives questions back for the next batch; how many.</summary>
        public async Task<int> ReleaseAsync(List<WorkflowOpenWait> waits, CancellationToken ct)
        {
            var released = 0;
            foreach (var wait in waits)
            {
                var data = wait.Data?.DeepClone().AsObject() ?? [];
                data.Remove(AiBatch.ClaimedBy);
                if (await bookmarks.SetDataAsync(AiBatch.WaitKind, wait.Key, data, ct))
                {
                    released++;
                }
            }

            return released;
        }

        /// <summary>Completes the steps' waits with the answer (<c>text</c>) or the <c>error</c>; how many were still open.</summary>
        public async Task<int> CompleteAsync(List<WorkflowOpenWait> waits, string? text, string? error, CancellationToken ct)
        {
            var count = 0;
            foreach (var wait in waits)
            {
                var payload = text is not null ? new JsonObject { ["text"] = text } : new JsonObject { ["error"] = error ?? "No answer." };
                if (await bookmarks.CompleteAsync(AiBatch.WaitKind, wait.Key, payload, ct))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
