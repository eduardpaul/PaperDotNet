using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Messaging;
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

    /// <summary>When queued questions are sent (cron, UTC), e.g. <c>0 1,13 * * *</c> for twice a day.</summary>
    public string Schedule { get; set; } = "0 1 * * *";
}

/// <summary>Waiting for and completing batch requests.</summary>
internal static class AiBatchRequests
{
    /// <summary>The kind of the bookmarks runs wait on; the key is <c>{request id}:{execution id}</c>.</summary>
    public const string WaitKind = "ai.batch";

    public const string MissedDeadline = "The batch did not answer by the deadline.";

    public static WorkflowActivityResult Wait(AiBatchRequest request, Guid executionId) =>
        WorkflowActivityResult.WaitAndRunAgain(WaitKind, $"{request.Id:N}:{executionId:N}", request.DeadlineAt);

    public static AiQuestion Question(AiBatchRequest request) => JsonSerializer.Deserialize<AiQuestion>(request.Question!)!;

    /// <summary>
    /// Completes the waits of runs on the finished <paramref name="requests"/> (tracked, not saved): the resume messages to
    /// save with them, so the answer and the runs going on are one transaction.
    /// </summary>
    public static async Task<List<ITenantMessage>> CompleteWaitersAsync(
        WorkflowsDbContext db, RunService runs, IReadOnlyCollection<AiBatchRequest> requests, CancellationToken ct)
    {
        var messages = new List<ITenantMessage>();
        if (requests.Count == 0)
        {
            return messages;
        }

        var prefixes = requests.Select(r => r.Id.ToString("N") + ":").ToHashSet();
        var open = await db.Bookmarks.Where(b => b.Kind == WaitKind && b.CompletedAt == null).ToListAsync(ct);
        foreach (var bookmark in open.Where(b => b.Key.Length > 33 && prefixes.Contains(b.Key[..33])))
        {
            runs.Complete(bookmark, null);
            messages.Add(runs.Resume(bookmark.RunId, bookmark.Id));
        }

        return messages;
    }

    /// <summary>Ends a request with an answer or an error and removes its question.</summary>
    public static void Finish(AiBatchRequest request, string? response, string? error, DateTimeOffset at)
    {
        request.Status = response is null ? AiBatchRequestStatus.Failed : AiBatchRequestStatus.Completed;
        request.PendingHash = null;
        request.Response = response;
        request.Error = error is null ? null : AiGateway.Truncate(error, 2000);
        request.Question = null;
        request.CompletedAt = at;
    }
}

/// <summary>
/// In the batch window (<see cref="AiBatchOptions.Schedule"/>) sends the organization's queued AI questions: in batches of
/// one model to the provider's batch API when an <see cref="IAiBatchClient"/> is registered (the results job collects the
/// answers), else answers them one by one with the chat model (deferred). Skipped while the day's AI budget is used up.
/// A batch is saved as preparing (with its requests) before it is sent, so a crash in between is recovered by looking it
/// up at the provider (<see cref="IAiBatchClient.FindAsync"/>) instead of paying for it twice.
/// </summary>
internal sealed partial class AiBatchSubmitJob(
    WorkflowsDbContext db, AiGateway ai, RunService runs, IOutbox outbox, TimeProvider time, ILogger<AiBatchSubmitJob> logger, IAiBatchClient? client = null)
    : ITenantRecurringJob
{
    public const string Name = "workflows.aiBatches";
    private const int Page = 100;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (await ai.BudgetProblemAsync(cancellationToken) is { } problem)
        {
            LogSkipped(problem);
            return;
        }

        if (client is null)
        {
            await AnswerAsync(cancellationToken);
            return;
        }

        var now = time.GetUtcNow();
        var models = await db.AiBatchRequests.Where(r => r.Status == AiBatchRequestStatus.Queued && r.DeadlineAt > now)
            .Select(r => r.Model).Distinct().ToListAsync(cancellationToken);
        foreach (var model in models)
        {
            while (await SubmitAsync(model, now, cancellationToken))
            {
            }
        }
    }

    /// <summary>Sends one batch of the model's queued requests; false when there were none (or they changed meanwhile).</summary>
    private async Task<bool> SubmitAsync(string model, DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var requests = await db.AiBatchRequests
            .Where(r => r.Status == AiBatchRequestStatus.Queued && r.Model == model && r.DeadlineAt > now)
            .OrderBy(r => r.CreatedAt).Take(Math.Max(1, client!.MaxLines)).ToListAsync(ct);
        if (requests.Count == 0)
        {
            return false;
        }

        var batch = new AiBatch { Id = Ids.New(), Model = model, Status = AiBatchPhase.Preparing, Lines = requests.Count, CreatedAt = time.GetUtcNow() };
        db.AiBatches.Add(batch);
        foreach (var request in requests)
        {
            request.Status = AiBatchRequestStatus.Submitted;
            request.BatchId = batch.Id;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Joined or answered meanwhile: the next round reads them again.
            return true;
        }

        var lines = requests.Select(r =>
        {
            var question = AiBatchRequests.Question(r);
            return new AiBatchLine(r.Id.ToString("N"), r.Model, question.Instructions, question.Input, question.Schema);
        }).ToList();

        // A failure here leaves the batch preparing: the results job looks it up and adopts it, or queues its requests again.
        batch.ProviderId = await client!.SubmitAsync(batch.Id, lines, ct);
        batch.Status = AiBatchPhase.Submitted;
        batch.SubmittedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        LogSubmitted(batch.Id, batch.ProviderId, requests.Count);
        return true;
    }

    /// <summary>Without a batch API: answers the queued requests with the chat model, each with its runs going on.</summary>
    private async Task AnswerAsync(CancellationToken ct)
    {
        var failed = new HashSet<Guid>();
        while (true)
        {
            db.ChangeTracker.Clear();
            var now = time.GetUtcNow();
            var requests = await db.AiBatchRequests.Where(r => r.Status == AiBatchRequestStatus.Queued && r.DeadlineAt > now && !failed.Contains(r.Id))
                .OrderBy(r => r.CreatedAt).Take(Page).ToListAsync(ct);
            if (requests.Count == 0)
            {
                return;
            }

            foreach (var request in requests)
            {
                db.ChangeTracker.Clear();
                db.AiBatchRequests.Attach(request);
                var (call, error) = await ai.CallAsync(AiBatchRequests.Question(request), request.Model, request.InputHash, "batch:" + request.Source, null, ct);
                if (call is null)
                {
                    LogSkipped(error!);
                    return;
                }

                AiBatchRequests.Finish(request, call.Response, call.Response is null ? error : null, time.GetUtcNow());
                try
                {
                    await outbox.SaveChangesAsync(db, [], await AiBatchRequests.CompleteWaitersAsync(db, runs, [request], ct), ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Changed meanwhile (joined): answered again in the next round, from the cache when it is on.
                    failed.Add(request.Id);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AI batch skipped: {Reason}")]
    private partial void LogSkipped(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI batch {BatchId} submitted as {ProviderId} with {Lines} lines")]
    private partial void LogSubmitted(Guid batchId, string providerId, int lines);
}

/// <summary>
/// Every few minutes: collects finished batches from the provider (answers recorded as <see cref="AiCall"/>s, waiting runs
/// go on; lines without an answer are queued again until their deadline), recovers batches left preparing by a crash,
/// and ends requests whose deadline passed (their runs call the model at once or fail, by <c>onDeadline</c>).
/// </summary>
internal sealed partial class AiBatchResultsJob(
    WorkflowsDbContext db, RunService runs, IOutbox outbox, TimeProvider time, ILogger<AiBatchResultsJob> logger, IAiBatchClient? client = null)
    : ITenantRecurringJob
{
    public const string Name = "workflows.aiBatchResults";
    public const string Schedule = "*/5 * * * *";

    /// <summary>A batch still preparing after this long was interrupted.</summary>
    public static readonly TimeSpan Interrupted = TimeSpan.FromMinutes(10);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ExpireAsync(cancellationToken);
        if (client is null)
        {
            return;
        }

        var interrupted = time.GetUtcNow() - Interrupted;
        foreach (var id in await db.AiBatches.Where(b => b.Status == AiBatchPhase.Preparing && b.CreatedAt < interrupted).Select(b => b.Id).ToListAsync(cancellationToken))
        {
            await RecoverAsync(id, cancellationToken);
        }

        foreach (var id in await db.AiBatches.Where(b => b.Status == AiBatchPhase.Submitted).OrderBy(b => b.SubmittedAt).Select(b => b.Id).ToListAsync(cancellationToken))
        {
            await CollectAsync(id, cancellationToken);
        }
    }

    /// <summary>Queued requests whose deadline passed are not sent any more.</summary>
    private async Task ExpireAsync(CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var now = time.GetUtcNow();
        var expired = await db.AiBatchRequests.Where(r => r.Status == AiBatchRequestStatus.Queued && r.DeadlineAt <= now).Take(500).ToListAsync(ct);
        foreach (var request in expired)
        {
            AiBatchRequests.Finish(request, null, AiBatchRequests.MissedDeadline, now);
        }

        await SaveAsync(expired, ct);
    }

    private async Task RecoverAsync(Guid batchId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var batch = await db.AiBatches.FirstAsync(b => b.Id == batchId, ct);
        if (await client!.FindAsync(batch.Id, ct) is { } providerId)
        {
            batch.ProviderId = providerId;
            batch.Status = AiBatchPhase.Submitted;
            batch.SubmittedAt = time.GetUtcNow();
            LogRecovered(batch.Id, providerId);
        }
        else
        {
            batch.Status = AiBatchPhase.Failed;
            batch.Error = "The batch was not sent.";
            batch.CompletedAt = time.GetUtcNow();
            foreach (var request in await db.AiBatchRequests.Where(r => r.BatchId == batch.Id && r.Status == AiBatchRequestStatus.Submitted).ToListAsync(ct))
            {
                request.Status = AiBatchRequestStatus.Queued;
                request.BatchId = null;
            }
        }

        await SaveAsync([], ct);
    }

    private async Task CollectAsync(Guid batchId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var batch = await db.AiBatches.FirstAsync(b => b.Id == batchId, ct);
        var status = await client!.GetAsync(batch.ProviderId!, ct);
        if (status.State == AiBatchState.Running)
        {
            return;
        }

        var now = time.GetUtcNow();
        var results = status.Results.GroupBy(r => r.CustomId).ToDictionary(g => g.Key, g => g.First());
        var finished = new List<AiBatchRequest>();
        foreach (var request in await db.AiBatchRequests.Where(r => r.BatchId == batch.Id && r.Status == AiBatchRequestStatus.Submitted).ToListAsync(ct))
        {
            if (results.TryGetValue(request.Id.ToString("N"), out var result))
            {
                var call = AiGateway.NewCall(request.Activity, $"batch:{batch.Id:N}", null, request.Model, request.InputHash, result.Error is null ? result.Text : null, false, now);
                call.InputTokens = result.InputTokens;
                call.OutputTokens = result.OutputTokens;
                call.Error = result.Error is null ? null : AiGateway.Truncate(result.Error, 2000);
                db.AiCalls.Add(call);
                AiBatchRequests.Finish(request, call.Response, call.Response is null ? result.Error ?? "The model gave no answer." : null, now);
                finished.Add(request);
            }
            else if (request.DeadlineAt > now)
            {
                request.Status = AiBatchRequestStatus.Queued;
                request.BatchId = null;
            }
            else
            {
                AiBatchRequests.Finish(request, null, AiBatchRequests.MissedDeadline, now);
                finished.Add(request);
            }
        }

        batch.Status = status.State == AiBatchState.Completed ? AiBatchPhase.Completed : AiBatchPhase.Failed;
        batch.Error = status.Error is null ? null : AiGateway.Truncate(status.Error, 2000);
        batch.CompletedAt = now;
        await SaveAsync(finished, ct);
        var answered = finished.Count(r => r.Status == AiBatchRequestStatus.Completed);
        LogCollected(batch.Id, answered, batch.Lines);
    }

    /// <summary>Saves the changes with the waits of the finished requests completed; a concurrent change is picked up next time.</summary>
    private async Task SaveAsync(IReadOnlyCollection<AiBatchRequest> finished, CancellationToken ct)
    {
        try
        {
            await outbox.SaveChangesAsync(db, [], await AiBatchRequests.CompleteWaitersAsync(db, runs, finished, ct), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI batch {BatchId} was interrupted and found at the provider as {ProviderId}")]
    private partial void LogRecovered(Guid batchId, string providerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI batch {BatchId} finished: {Answered} of {Lines} answered")]
    private partial void LogCollected(Guid batchId, int answered, int lines);
}
