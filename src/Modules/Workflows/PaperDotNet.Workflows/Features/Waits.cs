using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>Queries of bookmarks and approvals (precompiled: locals, one expression, explicit tenant, ADR-0039).</summary>
internal static class WaitQueries
{
    public static Task<WorkflowBookmark?> BookmarkAsync(WorkflowsDbContext database, Guid tenantId, string kind, string key, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var k = kind;
        var name = key;
        var ct = cancellationToken;
        return context.Bookmarks.FirstOrDefaultAsync(b => b.TenantId == tenant && b.Kind == k && b.Key == name, ct);
    }

    public static Task<WorkflowBookmark?> BookmarkByIdAsync(WorkflowsDbContext database, Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var bookmarkId = id;
        var ct = cancellationToken;
        return context.Bookmarks.FirstOrDefaultAsync(b => b.TenantId == tenant && b.Id == bookmarkId, ct);
    }

    /// <summary>The latest completed run-again wait of a node (handed back to its activity).</summary>
    public static Task<WorkflowBookmark?> ResumedAsync(WorkflowsDbContext database, Guid tenantId, Guid runId, string node, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var run = runId;
        var waiting = node;
        var ct = cancellationToken;
        return context.Bookmarks
            .Where(b => b.TenantId == tenant && b.RunId == run && b.Node == waiting && b.RunAgain && b.CompletedAtUnixMs != null)
            .OrderByDescending(b => b.CompletedAtUnixMs)
            .FirstOrDefaultAsync(ct);
    }

    public static Task<List<WorkflowBookmark>> OpenOfRunAsync(WorkflowsDbContext database, Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var run = runId;
        var ct = cancellationToken;
        return context.Bookmarks.Where(b => b.TenantId == tenant && b.RunId == run && b.CompletedAtUnixMs == null).ToListAsync(ct);
    }

    public static Task<List<WorkflowBookmark>> DueAsync(WorkflowsDbContext database, Guid tenantId, long nowUnixMs, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var now = nowUnixMs;
        var ct = cancellationToken;
        return context.Bookmarks
            .Where(b => b.TenantId == tenant && b.CompletedAtUnixMs == null && b.ResumeAtUnixMs != null && b.ResumeAtUnixMs <= now)
            .OrderBy(b => b.ResumeAtUnixMs).Take(500).ToListAsync(ct);
    }

    public static Task<List<WorkflowBookmark>> OpenOfKindAsync(
        WorkflowsDbContext database, Guid tenantId, string kind, Guid workspaceId, int skip, int take, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var k = kind;
        var workspace = workspaceId;
        var first = skip;
        var count = take;
        var ct = cancellationToken;
        return context.Bookmarks.AsNoTracking()
            .Where(b => b.TenantId == tenant && b.Kind == k && b.CompletedAtUnixMs == null
                && context.WorkflowRuns.Any(r => r.TenantId == tenant && r.Id == b.RunId && r.WorkspaceId == workspace))
            .OrderBy(b => b.CreatedAtUnixMs).ThenBy(b => b.Id)
            .Skip(first).Take(count)
            .ToListAsync(ct);
    }

    /// <summary>Completions no run waited for, older than <paramref name="beforeUnixMs"/>.</summary>
    public static Task<List<WorkflowBookmark>> UnclaimedAsync(WorkflowsDbContext database, Guid tenantId, long beforeUnixMs, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var before = beforeUnixMs;
        var none = Guid.Empty;
        var ct = cancellationToken;
        return context.Bookmarks.Where(b => b.TenantId == tenant && b.RunId == none && b.CompletedAtUnixMs < before).Take(500).ToListAsync(ct);
    }

    public static Task<ApprovalRequest?> ApprovalAsync(WorkflowsDbContext database, Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var approvalId = id;
        var ct = cancellationToken;
        return context.Approvals.FirstOrDefaultAsync(a => a.TenantId == tenant && a.Id == approvalId, ct);
    }

    /// <summary>The pending request of a node of a run (reused when the node runs again).</summary>
    public static Task<ApprovalRequest?> PendingOfNodeAsync(WorkflowsDbContext database, Guid tenantId, Guid runId, string node, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var run = runId;
        var name = node;
        var pending = ApprovalStatus.Pending;
        var ct = cancellationToken;
        return context.Approvals.FirstOrDefaultAsync(a => a.TenantId == tenant && a.RunId == run && a.Node == name && a.Status == pending, ct);
    }

    public static Task<List<ApprovalRequest>> PendingOfRunAsync(WorkflowsDbContext database, Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var run = runId;
        var pending = ApprovalStatus.Pending;
        var ct = cancellationToken;
        return context.Approvals.Where(a => a.TenantId == tenant && a.RunId == run && a.Status == pending).ToListAsync(ct);
    }

    public static Task<List<ApprovalRequest>> OverdueAsync(WorkflowsDbContext database, Guid tenantId, long nowUnixMs, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var now = nowUnixMs;
        var pending = ApprovalStatus.Pending;
        var ct = cancellationToken;
        return context.Approvals
            .Where(a => a.TenantId == tenant && a.Status == pending && !a.Escalated && a.DueAtUnixMs != null && a.DueAtUnixMs <= now)
            .Take(100).ToListAsync(ct);
    }

    /// <summary>Requests the user is an assignee of, newest first; <paramref name="status"/> null for all.</summary>
    public static Task<List<ApprovalRequest>> OfAssigneeAsync(WorkflowsDbContext database, Guid tenantId, Guid userId, string status, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var pattern = $"\"{userId}\"";
        var wanted = status;
        var ct = cancellationToken;
        return context.Approvals.AsNoTracking()
            .Where(a => a.TenantId == tenant && a.Status == wanted && a.Assignees.Contains(pattern))
            .OrderByDescending(a => a.Id).Take(200).ToListAsync(ct);
    }

    public static Task<List<ApprovalRequest>> AllOfAssigneeAsync(WorkflowsDbContext database, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var pattern = $"\"{userId}\"";
        var ct = cancellationToken;
        return context.Approvals.AsNoTracking()
            .Where(a => a.TenantId == tenant && a.Assignees.Contains(pattern))
            .OrderByDescending(a => a.Id).Take(200).ToListAsync(ct);
    }

    public static Task<WorkflowRun?> RunAsync(WorkflowsDbContext database, Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = runId;
        var ct = cancellationToken;
        return context.WorkflowRuns.FirstOrDefaultAsync(r => r.TenantId == tenant && r.Id == id, ct);
    }
}

/// <summary>User id lists of approvals, stored as JSON arrays of ids.</summary>
internal static class UserIds
{
    public static string ToJson(IEnumerable<Guid> ids) => new JsonArray([.. ids.Distinct().Select(id => (JsonNode)JsonValue.Create(id.ToString()))]).ToJsonString();

    public static List<Guid> FromJson(string json) =>
        JsonNode.Parse(json) is JsonArray array
            ? [.. array.Select(v => v is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)]
            : [];
}

/// <summary>
/// Completes waits of runs for other modules and extensions (<see cref="IWorkflowBookmarks"/>, ADR-0036). The bookmark and
/// the message that resumes its run are saved together. A completion that arrives before the run has saved its wait is
/// kept as a bookmark without a run (<see cref="Unclaimed"/>), which the run takes over when it starts waiting. Each call
/// works in its own scope, so an activity can use it during a run without saving (or clearing) the run's state.
/// </summary>
internal sealed class WorkflowBookmarks(IServiceScopeFactory scopes, TimeProvider time) : IWorkflowBookmarks
{
    /// <summary>The run id of a completion that no run waits for yet.</summary>
    public static readonly Guid Unclaimed = Guid.Empty;

    public async Task<bool> CompleteAsync(Guid tenantId, string kind, string key, JsonObject? payload, CancellationToken cancellationToken)
    {
        CheckWait(kind, key);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        for (var attempt = 0; ; attempt++)
        {
            var bookmark = await WaitQueries.BookmarkAsync(db, tenantId, kind, key, cancellationToken);
            try
            {
                if (bookmark is null)
                {
                    var now = time.GetUtcNow();
                    db.Bookmarks.Add(new WorkflowBookmark
                    {
                        Id = Ids.New(),
                        TenantId = tenantId,
                        RunId = Unclaimed,
                        Kind = kind,
                        Key = key,
                        CreatedAt = now,
                        CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
                        CompletedAt = now,
                        CompletedAtUnixMs = now.ToUnixTimeMilliseconds(),
                        Payload = payload?.ToJsonString(),
                    });
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                }

                if (bookmark.CompletedAtUnixMs is not null)
                {
                    return false;
                }

                RunService.Complete(bookmark, payload, time);
                await outbox.SaveChangesAsync(db, [], [new ResumeRun(tenantId, bookmark.RunId)], cancellationToken);
                return true;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Created or completed concurrently (unique kind and key, or the version): look again.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<WorkflowOpenWait>> ListOpenAsync(Guid tenantId, string kind, Guid workspaceId, int skip, int take, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var waits = await WaitQueries.OpenOfKindAsync(db, tenantId, kind, workspaceId, Math.Max(0, skip), Math.Clamp(take, 1, 1000), cancellationToken);
        return [.. waits.Select(w => new WorkflowOpenWait(
            w.Key, w.RunId, w.Data is { } json ? JsonNode.Parse(json) as JsonObject : null, w.CreatedAt,
            w.ResumeAtUnixMs is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null))];
    }

    public async Task<bool> SetDataAsync(Guid tenantId, string kind, string key, JsonObject? data, CancellationToken cancellationToken)
    {
        CheckWait(kind, key);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var bookmark = await WaitQueries.BookmarkAsync(db, tenantId, kind, key, cancellationToken);
        if (bookmark is null || bookmark.CompletedAtUnixMs is not null)
        {
            return false;
        }

        bookmark.Data = data?.ToJsonString();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>Checks the kind and key of a wait; the engine's own kinds cannot be completed from outside.</summary>
    public static void CheckWait(string kind, string key)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 100 || BookmarkKinds.Reserved.Contains(kind))
        {
            throw new ArgumentException($"'{kind}' is not a kind of wait others can complete (1 to 100 characters, not {string.Join(", ", BookmarkKinds.Reserved)}).", nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        {
            throw new ArgumentException("A wait key has 1 to 200 characters.", nameof(key));
        }
    }
}

/// <summary>Values of approval decisions (the approval node's ports).</summary>
public static class ApprovalOutcomes
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>Decisions on approvals, completing bookmarks, and cancelling and retrying runs.</summary>
internal sealed class RunService(WorkflowsDbContext db, IOutbox outbox, IItemActivity activity, TimeProvider time)
{
    public enum DecisionResult
    {
        Ok,
        NotFound,
        AlreadyDecided,
    }

    /// <summary>Marks a tracked bookmark completed with its payload (the caller saves it with the resume message).</summary>
    public static void Complete(WorkflowBookmark bookmark, JsonObject? payload, TimeProvider time)
    {
        var now = time.GetUtcNow();
        bookmark.CompletedAt = now;
        bookmark.CompletedAtUnixMs = now.ToUnixTimeMilliseconds();
        bookmark.Payload = payload?.ToJsonString();
    }

    public static string ApprovalKey(Guid approvalId) => approvalId.ToString("N");

    /// <summary>
    /// Records the decision of an assignee and completes the approval's bookmark; the message that resumes the run is
    /// stored with it. A concurrent change of the request (e.g. an escalation adding assignees) is retried.
    /// </summary>
    public async Task<DecisionResult> DecideAsync(Guid tenantId, Guid approvalId, Guid userId, string outcome, string? comment, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var approval = await WaitQueries.ApprovalAsync(db, tenantId, approvalId, ct);
            if (approval is null || !UserIds.FromJson(approval.Assignees).Contains(userId))
            {
                return DecisionResult.NotFound;
            }

            if (approval.Status != ApprovalStatus.Pending)
            {
                return DecisionResult.AlreadyDecided;
            }

            var now = time.GetUtcNow();
            approval.Status = outcome == ApprovalOutcomes.Approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
            approval.DecidedBy = userId;
            approval.DecidedAt = now;
            approval.Comment = comment;
            var messages = new List<object>();
            if (await WaitQueries.BookmarkAsync(db, tenantId, BookmarkKinds.Approval, ApprovalKey(approval.Id), ct) is { CompletedAtUnixMs: null } bookmark)
            {
                Complete(bookmark, new JsonObject { ["outcome"] = outcome, ["decidedBy"] = userId.ToString(), ["comment"] = comment }, time);
                messages.Add(new ResumeRun(tenantId, bookmark.RunId));
            }

            try
            {
                await outbox.SaveChangesAsync(db, [], messages, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
                continue;
            }

            if (approval is { ListId: { } listId, ItemId: { } itemId })
            {
                var summary = $"{approval.Node}: {outcome}" + (comment is { Length: > 0 } ? $" ({comment})" : "");
                await activity.RecordAsync(new ChangeActor(tenantId, userId),
                    new ItemActivityEntry(approval.WorkspaceId, listId, itemId, ActivityKinds.Approval, summary, $"approval:{approval.Id:N}"), ct);
            }

            return DecisionResult.Ok;
        }
    }

    /// <summary>
    /// Stops a run, cancels its pending approvals and removes its open waits. Retries when the run was changed
    /// concurrently; messages for the run that arrive later find it cancelled and do nothing.
    /// </summary>
    public async Task CancelAsync(WorkflowRun run, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            run.Status = RunStatus.Cancelled;
            run.WaitingOn = null;
            run.CompletedAt = time.GetUtcNow();
            foreach (var approval in await WaitQueries.PendingOfRunAsync(db, run.TenantId, run.Id, ct))
            {
                approval.Status = ApprovalStatus.Cancelled;
            }

            db.Bookmarks.RemoveRange(await WaitQueries.OpenOfRunAsync(db, run.TenantId, run.Id, ct));
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
                run = (await WaitQueries.RunAsync(db, run.TenantId, run.Id, ct))!;
                if (run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Runs a failed run again from the node where it failed. The node's action keeps its execution id, so what an
    /// earlier attempt did is not repeated. False when the run cannot be retried.
    /// </summary>
    public async Task<bool> RetryAsync(WorkflowRun run, CancellationToken ct)
    {
        if (run is not { Status: RunStatus.Failed, FailedNode: { } node })
        {
            return false;
        }

        var now = time.GetUtcNow();
        run.Status = RunStatus.Running;
        run.Node = node;
        run.FailedNode = null;
        run.Error = null;
        run.CompletedAt = null;
        run.NodesRun = 0;
        var log = JsonNode.Parse(run.Log) as JsonArray ?? [];
        log.Add((JsonNode)new JsonObject { ["at"] = now.ToString("O"), ["message"] = $"Retried from {node}" });
        run.Log = log.ToJsonString();
        await outbox.SaveChangesAsync(db, [], [new ResumeRun(run.TenantId, run.Id)], ct);
        return true;
    }
}

/// <summary>Notifies the assignees of an approval: sent with the request, and again when it is escalated.</summary>
public sealed record NotifyApproval(Guid TenantId, Guid ApprovalId, bool Overdue);

/// <summary>Wolverine handler of <see cref="NotifyApproval"/> (generated ahead of time); notifications are deduplicated per approval.</summary>
public static class NotifyApprovalSubscriber
{
    public static async Task Handle(NotifyApproval message, WorkflowsDbContext db, INotificationSender sender, CancellationToken cancellationToken)
    {
        if (await WaitQueries.ApprovalAsync(db, message.TenantId, message.ApprovalId, cancellationToken) is not { Status: ApprovalStatus.Pending } approval)
        {
            return;
        }

        var link = approval.ListId is { } listId ? new NotificationLink(approval.WorkspaceId, listId, approval.ItemId) : null;
        var notification = message.Overdue
            ? new NotificationMessage(NotificationTypes.Workflow, $"Overdue: {approval.Title}", "The approval is overdue.", link, $"approval:{approval.Id:N}:overdue")
            : new NotificationMessage(NotificationTypes.Workflow, approval.Title, "An approval is waiting for your decision.", link, $"approval:{approval.Id:N}");
        await sender.SendAsync(message.TenantId, notification, UserIds.FromJson(approval.Assignees), cancellationToken);
    }
}

/// <summary>
/// Every minute: completes bookmarks whose time has come (delays, retries, time-outs of other waits) and resumes their
/// runs, and escalates overdue approvals (adds <c>escalateTo</c> as assignees and notifies everyone).
/// </summary>
internal sealed class WorkflowTimerJob(WorkflowsDbContext db, IOutbox outbox, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "workflows.timers";
    public const string Schedule = "* * * * *";

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var due = await WaitQueries.DueAsync(db, tenantId, now, cancellationToken);
        if (due.Count > 0)
        {
            foreach (var bookmark in due)
            {
                // A delay or retry is simply over; any other wait timed out (port timeout, else done).
                RunService.Complete(bookmark, bookmark.Kind is BookmarkKinds.Delay or BookmarkKinds.Retry ? null : new JsonObject { ["outcome"] = "timeout" }, time);
            }

            try
            {
                await outbox.SaveChangesAsync(db, [], [.. due.Select(b => (object)new ResumeRun(tenantId, b.RunId))], cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Completed (or removed) meanwhile: the rest is picked up next minute.
                db.ChangeTracker.Clear();
            }
        }

        foreach (var approval in await WaitQueries.OverdueAsync(db, tenantId, now, cancellationToken))
        {
            approval.Escalated = true;
            var assignees = UserIds.FromJson(approval.Assignees);
            approval.Assignees = UserIds.ToJson([.. assignees, .. UserIds.FromJson(approval.EscalateTo)]);
            try
            {
                await outbox.SaveChangesAsync(db, [], [new NotifyApproval(tenantId, approval.Id, true)], cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Decided (or changed) meanwhile: a still pending request is escalated next minute.
                db.ChangeTracker.Clear();
            }
        }
    }
}
