using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Automation.Data;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Notifications.Contracts;

namespace PaperDotNet.Automation.Features;

/// <summary>
/// Resumes an automation run (ADR-0019, ADR-0024): sent with the run when it starts, with a decision when an approval
/// is decided, and by the minute job when a delay is over. <see cref="WaitKey"/> names the wait it ends (null for
/// the start); messages for a wait the run no longer has are ignored, so duplicates and stale ones are harmless.
/// </summary>
public sealed record ResumeRun(Guid RunId, string? WaitKey, Guid TenantId, string TenantIdentifier, Guid? UserId = null) : ITenantMessage;

/// <summary>Wolverine handler for <see cref="ResumeRun"/> (discovered by convention).</summary>
public static class ResumeRunHandler
{
    public static async Task Handle(ResumeRun message, ITenantScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateScope(message.TenantId, message.TenantIdentifier);
        await scope.ServiceProvider.GetRequiredService<AutomationInterpreter>().RunAsync(message.RunId, message.WaitKey, cancellationToken);
    }
}

/// <summary>A run to start; with an <see cref="Error"/> (e.g. a condition that cannot be checked) it is saved as failed.</summary>
internal sealed record AutomationStart(
    AutomationDefinition Automation, AutomationItem? Item, Guid? EventId, string? Data, int Depth, Guid? StartedBy, string? Error = null);

/// <summary>Starts runs of a workspace's automations.</summary>
internal sealed class AutomationStarter(
    AutomationDbContext db, IListItemStore items, IOutbox outbox, EventCausation causation, ITenantContext tenant, TimeProvider time)
{
    /// <summary>Whether the item matches an OData condition; an error when the condition cannot be checked.</summary>
    public async Task<(bool Matches, string? Error)> CheckConditionAsync(AutomationItem item, string condition, CancellationToken ct)
    {
        var (matches, error) = await items.AsSystem().QueryAsync(
            item.WorkspaceId, item.ListId, new ListItemQuery($"id eq {item.ItemId} and ({condition})", Top: 1), ct);
        return (error is null && matches.Count > 0, error is null ? null : $"condition: {error}");
    }

    /// <summary>Saves the runs together with the messages that start them (transactional outbox).</summary>
    public async Task<List<AutomationRun>> StartAsync(IReadOnlyList<AutomationStart> starts, CancellationToken ct)
    {
        if (starts.Count == 0)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var runs = new List<AutomationRun>();
        var messages = new List<ITenantMessage>();
        foreach (var start in starts)
        {
            var run = new AutomationRun
            {
                Id = Ids.New(),
                AutomationId = start.Automation.Id,
                AutomationVersion = start.Automation.CurrentVersion,
                WorkspaceId = start.Automation.WorkspaceId,
                ListId = start.Item?.ListId,
                ItemId = start.Item?.ItemId,
                EventId = start.EventId,
                Data = start.Data,
                Status = RunStatus.Running,
                Depth = start.Depth,
                StartedBy = start.StartedBy,
                StartedAt = now,
                LastActivityAt = now,
            };
            if (start.Error is { } error)
            {
                run.Status = RunStatus.Failed;
                run.Error = AutomationInterpreter.Truncate(error);
                run.CompletedAt = now;
                run.Log = new JsonArray(new JsonObject { ["at"] = now.ToString("O"), ["message"] = $"Failed: {run.Error}" }).ToJsonString();
            }
            else
            {
                messages.Add(new ResumeRun(run.Id, null, tenant.TenantId!.Value, tenant.TenantIdentifier!));
            }

            db.Runs.Add(run);
            runs.Add(run);
        }

        await outbox.SaveChangesAsync(db, [], messages, ct);
        return runs;
    }

    /// <summary>Starts an automation with the <c>manual</c> trigger on an item, by name.</summary>
    public async Task<(AutomationRun? Run, string? Error)> StartManualAsync(AutomationItem item, string name, Guid? startedBy, CancellationToken ct)
    {
        var automation = await db.Automations.AsNoTracking().FirstOrDefaultAsync(a => a.WorkspaceId == item.WorkspaceId && a.Name == name, ct);
        if (automation is null)
        {
            return (null, $"The automation '{name}' does not exist in the workspace.");
        }

        if (automation.Trigger != AutomationTriggers.Manual)
        {
            return (null, $"The automation '{name}' is not started manually (its trigger is {automation.Trigger}).");
        }

        if (!automation.Enabled)
        {
            return (null, $"The automation '{name}' is disabled.");
        }

        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.AutomationId == automation.Id && v.Number == automation.CurrentVersion, ct);
        var spec = DefinitionJson.Deserialize<AutomationSpec>(version.Definition);
        var store = items.AsSystem();
        var list = await store.GetListAsync(item.WorkspaceId, item.ListId, ct);
        if (spec.Trigger.List is { } listName && list?.Name != listName)
        {
            return (null, $"The automation '{name}' only runs on items of the list '{listName}'.");
        }

        if (spec.Trigger.ContentType is { } type)
        {
            var data = await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, ct);
            var contentType = list?.ContentTypes.FirstOrDefault(c => c.Id == data?.ContentTypeId);
            if (contentType?.Name != type && contentType?.Key != type)
            {
                return (null, $"The automation '{name}' only runs on items of the content type '{type}'.");
            }
        }

        if (spec.Condition is { } condition)
        {
            var (matches, error) = await CheckConditionAsync(item, condition, ct);
            if (!matches)
            {
                return (null, error ?? "The item does not match the automation's condition.");
            }
        }

        var runs = await StartAsync([new AutomationStart(automation, item, null, null, causation.Depth, startedBy)], ct);
        return (runs[0], null);
    }
}

/// <summary>The run is being executed by another handler (its lease has not expired); the message is retried later.</summary>
public sealed class RunLeasedException : Exception
{
    public RunLeasedException()
    {
    }

    public RunLeasedException(string message)
        : base(message)
    {
    }

    public RunLeasedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Notifies the assignees of an approval (sent with the approval, or when it was escalated).</summary>
public sealed record NotifyApproval(Guid ApprovalId, bool Overdue, Guid TenantId, string TenantIdentifier, Guid? UserId = null) : ITenantMessage;

/// <summary>Wolverine handler for <see cref="NotifyApproval"/>: notifications are deduplicated by approval, so retries are harmless.</summary>
public static class NotifyApprovalHandler
{
    public static async Task Handle(NotifyApproval message, ITenantScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateScope(message.TenantId, message.TenantIdentifier);
        var db = scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == message.ApprovalId, cancellationToken);
        if (approval is not { Status: ApprovalStatus.Pending })
        {
            return;
        }

        var link = new NotificationLink(approval.WorkspaceId, approval.ListId, approval.ItemId);
        var notification = message.Overdue
            ? new NotificationMessage(NotificationTypes.Automation, $"Overdue: {approval.Title}", "The approval is overdue.", link, $"approval:{approval.Id:N}:overdue")
            : new NotificationMessage(NotificationTypes.Automation, approval.Title, "An approval is waiting for your decision.", link, $"approval:{approval.Id:N}");
        await scope.ServiceProvider.GetRequiredService<INotificationSender>().SendAsync(notification, approval.Assignees, cancellationToken);
    }
}

/// <summary>
/// Runs an automation's compiled steps from the run's position until it finishes or has to wait (EVT-07, EVT-08).
/// <list type="bullet">
/// <item>A handler first claims the run with a lease (a conditional update), so only one server executes it at a time;
/// a busy run makes the message retry later. A crashed handler's lease expires and the timer job resumes the run.</item>
/// <item>Progress is saved after every step, so a retried execution continues where it stopped. An action step gets a
/// stable execution id and key before it runs, the same when it runs again.</item>
/// <item>A run that keeps failing to make progress is failed after <see cref="MaxAttempts"/> attempts.</item>
/// </list>
/// </summary>
internal sealed partial class AutomationInterpreter(
    AutomationDbContext db,
    ActionExecutor executor,
    IListItemStore items,
    RecipientResolver recipients,
    TokenExpander tokens,
    IOutbox outbox,
    EventCausation causation,
    ITenantContext tenant,
    TimeProvider time,
    ILogger<AutomationInterpreter> logger)
{
    public const int MaxAttempts = 10;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private const int MaxInstructionsPerRun = 500;
    private const int MaxLogEntries = 100;

    /// <summary>Continues the run when <paramref name="waitKey"/> is the wait it is in (null: not started or running).</summary>
    public async Task RunAsync(Guid runId, string? waitKey, CancellationToken ct)
    {
        var current = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (current is null || IsFinished(current.Status) || current.WaitingFor != waitKey
            || (current.ResumeAt is { } resumeAt && resumeAt > time.GetUtcNow()))
        {
            return;
        }

        var lease = Ids.New();
        var now = time.GetUtcNow();
        var claimed = await db.Runs
            .Where(r => r.Id == runId && r.WaitingFor == waitKey && (r.LeaseUntil == null || r.LeaseUntil < now)
                && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseId, lease).SetProperty(r => r.LeaseUntil, now + Lease).SetProperty(r => r.Attempts, r => r.Attempts + 1), ct);
        if (claimed == 0)
        {
            var again = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (again is null || IsFinished(again.Status) || again.WaitingFor != waitKey)
            {
                return;
            }

            throw new RunLeasedException($"The run {runId} is being executed elsewhere.");
        }

        try
        {
            await ExecuteAsync(runId, lease, ct);
        }
        catch
        {
            // Let the retry (or another server) claim the run right away.
            await db.Runs.Where(r => r.Id == runId && r.LeaseId == lease)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseId, (Guid?)null).SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null), CancellationToken.None);
            throw;
        }
    }

    private static bool IsFinished(RunStatus status) => status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;

    private async Task ExecuteAsync(Guid runId, Guid lease, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var run = await db.Runs.FirstAsync(r => r.Id == runId, ct);
        causation.Depth = run.Depth + 1;
        var automation = await db.Automations.AsNoTracking().FirstAsync(a => a.Id == run.AutomationId, ct);
        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.AutomationId == run.AutomationId && v.Number == run.AutomationVersion, ct);
        var program = Definitions.Compile(DefinitionJson.Deserialize<AutomationSpec>(version.Definition).Steps);
        var outcomes = DefinitionJson.Deserialize<Dictionary<string, string>>(run.Outcomes);
        var log = JsonNode.Parse(run.Log) as JsonArray ?? [];
        var item = run.ListId is { } listId && run.ItemId is { } itemId ? new AutomationItem(run.WorkspaceId, listId, itemId) : null;
        var data = run.Data is { } json ? JsonNode.Parse(json) as JsonObject : null;
        void Log(string message)
        {
            log.Add(new JsonObject { ["at"] = time.GetUtcNow().ToString("O"), ["message"] = message });
            while (log.Count > MaxLogEntries)
            {
                log.RemoveAt(0);
            }

            run.Log = log.ToJsonString();
            run.Outcomes = DefinitionJson.Serialize(outcomes);
        }

        // Saves progress and extends the lease; releases it when the run stops (finished or waiting).
        Task SaveAsync(IReadOnlyCollection<ITenantMessage>? messages = null)
        {
            var now = time.GetUtcNow();
            run.LastActivityAt = now;
            var stopped = run.Status != RunStatus.Running;
            run.LeaseId = stopped ? null : lease;
            run.LeaseUntil = stopped ? null : now + Lease;
            return messages is { Count: > 0 } ? outbox.SaveChangesAsync(db, [], messages, ct) : db.SaveChangesAsync(ct);
        }

        void Advance(int position)
        {
            run.Position = position;
            run.StepExecutionId = null;
            run.Attempts = 0;
        }

        async Task FailAsync(string error)
        {
            Log($"Failed: {error}");
            run.Status = RunStatus.Failed;
            run.Error = Truncate(error);
            run.WaitingFor = null;
            run.ResumeAt = null;
            run.CompletedAt = time.GetUtcNow();
            await SaveAsync();
            LogRunFailed(run.Id, error);
        }

        if (run.Attempts > MaxAttempts)
        {
            await FailAsync($"The run stopped after {MaxAttempts} attempts without progress (see the server log).");
            return;
        }

        if (run.Status == RunStatus.Waiting && run.Position < program.Count)
        {
            var waiting = program[run.Position];
            if (waiting.Op == OpCode.Approval)
            {
                var decided = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.RunId == run.Id && a.StepName == waiting.StepName && a.Status != ApprovalStatus.Pending && a.Status != ApprovalStatus.Cancelled, ct);
                if (decided is null)
                {
                    // Nothing to do yet (e.g. a duplicate message); not a failed attempt.
                    run.Attempts = 0;
                    await SaveAsync();
                    return;
                }

                var outcome = decided.Status == ApprovalStatus.Approved ? ApprovalOutcomes.Approved : ApprovalOutcomes.Rejected;
                outcomes[waiting.StepName] = outcome;
                Log($"{waiting.StepName}: {outcome}");
            }

            Advance(run.Position + 1);
            run.Status = RunStatus.Running;
            run.WaitingFor = null;
            run.ResumeAt = null;
            run.NextCheckAt = null;
            await SaveAsync();
        }

        for (var executed = 0; run.Position < program.Count; executed++)
        {
            if (executed >= MaxInstructionsPerRun)
            {
                await FailAsync("The automation ran too many steps.");
                return;
            }

            var instruction = program[run.Position];
            switch (instruction.Op)
            {
                case OpCode.Action:
                    var action = (ActionNode)instruction.Step;
                    if (run.StepExecutionId is null)
                    {
                        // Stable across retries of this step: actions use it to be safe to repeat.
                        run.StepExecutionId = Ids.New();
                        await SaveAsync();
                    }

                    var result = await executor.ExecuteAsync(
                        new ActionDefinition(action.Action!, action.Inputs), run.WorkspaceId, item, run.StartedBy, data, outcomes,
                        $"automation:{automation.Name}", $"run:{run.Id:N}:{run.Position}", run.StepExecutionId.Value, ct);
                    if (!result.Succeeded)
                    {
                        await FailAsync($"{instruction.StepName} ({action.Action}): {result.Error}");
                        return;
                    }

                    Log($"{instruction.StepName}: {action.Action} done");
                    Advance(run.Position + 1);
                    break;
                case OpCode.Approval:
                    if (item is null)
                    {
                        await FailAsync($"{instruction.StepName}: an approval needs an item (the trigger has none).");
                        return;
                    }

                    var approval = await CreateApprovalAsync(run, item, instruction, outcomes, ct);
                    if (approval is null)
                    {
                        await FailAsync($"{instruction.StepName}: no assignee could be found.");
                        return;
                    }

                    // The request, the wait and the notification are saved together: a decision can never find the run
                    // not yet waiting for it, and the assignees are always told.
                    run.Status = RunStatus.Waiting;
                    run.WaitingFor = WaitKey(approval.Id);
                    Log($"{instruction.StepName}: waiting for approval");
                    await SaveAsync([new NotifyApproval(approval.Id, false, tenant.TenantId!.Value, tenant.TenantIdentifier!)]);
                    return;
                case OpCode.Delay:
                    var delay = (DelayNode)instruction.Step;
                    run.Status = RunStatus.Waiting;
                    run.WaitingFor = DelayKey(run.Id, run.Position);
                    run.ResumeAt = time.GetUtcNow().AddHours(delay.Hours!.Value);
                    run.NextCheckAt = null;
                    Log($"{instruction.StepName}: waiting {delay.Hours} hours");
                    await SaveAsync();
                    return;
                case OpCode.Branch:
                    var condition = (ConditionNode)instruction.Step;
                    var (holds, error) = await EvaluateAsync(condition, item, outcomes, ct);
                    if (error is not null)
                    {
                        await FailAsync($"{instruction.StepName}: {error}");
                        return;
                    }

                    Advance(holds ? run.Position + 1 : instruction.Target);
                    break;
                case OpCode.Jump:
                    Advance(instruction.Target);
                    break;
            }

            await SaveAsync();
        }

        run.Status = RunStatus.Completed;
        run.CompletedAt = time.GetUtcNow();
        Log("Completed");
        await SaveAsync();
    }

    public static string WaitKey(Guid approvalId) => $"approval:{approvalId:N}";

    public static string DelayKey(Guid runId, int position) => $"delay:{runId:N}:{position}";

    public static string Truncate(string text) => text.Length > 2000 ? text[..2000] : text;

    private async Task<(bool Holds, string? Error)> EvaluateAsync(ConditionNode step, AutomationItem? item, Dictionary<string, string> outcomes, CancellationToken ct)
    {
        if (step.ApprovalName is { } name)
        {
            return (outcomes.TryGetValue(name, out var outcome) && outcome == step.Outcome, null);
        }

        if (item is null)
        {
            return (false, "a filter needs an item (the trigger has none).");
        }

        var (matches, error) = await items.AsSystem().QueryAsync(item.WorkspaceId, item.ListId, new ListItemQuery($"id eq {item.ItemId} and ({step.Filter})", Top: 1), ct);
        return (matches.Count > 0, error);
    }

    /// <summary>The pending request of this step (reused when the step runs again), or a new one (not saved yet).</summary>
    private async Task<ApprovalRequest?> CreateApprovalAsync(AutomationRun run, AutomationItem item, Instruction instruction, Dictionary<string, string> outcomes, CancellationToken ct)
    {
        var existing = await db.Approvals.FirstOrDefaultAsync(a => a.RunId == run.Id && a.StepName == instruction.StepName && a.Status == ApprovalStatus.Pending, ct);
        if (existing is not null)
        {
            return existing;
        }

        var step = (ApprovalNode)instruction.Step;
        var current = await items.AsSystem().GetAsync(item.WorkspaceId, item.ListId, item.ItemId, ct);
        var (assignees, _) = await recipients.ResolveAsync(step.Assignees ?? [], current, run.StartedBy, ct);
        if (assignees.Count == 0)
        {
            return null;
        }

        var (escalateTo, _) = await recipients.ResolveAsync(step.EscalateTo ?? [], current, run.StartedBy, ct);
        var list = await items.AsSystem().GetListAsync(item.WorkspaceId, item.ListId, ct);
        var scope = new TokenScope(current, list?.Name, outcomes, run.Data is { } data ? JsonNode.Parse(data) as JsonObject : null);
        var title = await tokens.ExpandAsync(step.Title ?? $"Approve {{title}} ({instruction.StepName})", scope, ct);
        var approval = new ApprovalRequest
        {
            Id = Ids.New(),
            RunId = run.Id,
            StepName = instruction.StepName,
            WorkspaceId = run.WorkspaceId,
            ListId = item.ListId,
            ItemId = item.ItemId,
            Title = title.Length > 1000 ? title[..1000] : title,
            Assignees = assignees,
            EscalateTo = [.. escalateTo.Except(assignees)],
            DueAt = step.DueInHours is { } hours ? time.GetUtcNow().AddHours(hours) : null,
            Status = ApprovalStatus.Pending,
        };
        db.Approvals.Add(approval);
        return approval;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation run {RunId} failed: {Error}")]
    private partial void LogRunFailed(Guid runId, string error);
}

/// <summary>Decisions on approvals and cancelling runs.</summary>
internal sealed class ApprovalService(AutomationDbContext db, IOutbox outbox, ITenantContext tenant, TimeProvider time, IItemActivity activity)
{
    public enum DecisionResult
    {
        Ok,
        NotFound,
        AlreadyDecided,
    }

    /// <summary>
    /// Records the decision of an assignee; the message that resumes the run is stored with it. A concurrent change of
    /// the request (e.g. an escalation adding assignees) is retried.
    /// </summary>
    public async Task<DecisionResult> DecideAsync(Guid approvalId, Guid userId, string outcome, string? comment, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == approvalId, ct);
            if (approval is null || !approval.Assignees.Contains(userId))
            {
                return DecisionResult.NotFound;
            }

            if (approval.Status != ApprovalStatus.Pending)
            {
                return DecisionResult.AlreadyDecided;
            }

            approval.Status = outcome == ApprovalOutcomes.Approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
            approval.DecidedBy = userId;
            approval.DecidedAt = time.GetUtcNow();
            approval.Comment = comment;
            try
            {
                await outbox.SaveChangesAsync(db, [], [Resume(approval.RunId, AutomationInterpreter.WaitKey(approval.Id))], ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
                continue;
            }

            var summary = $"{approval.StepName}: {(approval.Status == ApprovalStatus.Approved ? "approved" : "rejected")}" + (comment is { Length: > 0 } ? $" ({comment})" : "");
            await activity.RecordAsync(
                new ItemActivityEntry(approval.WorkspaceId, approval.ListId, approval.ItemId, ActivityKinds.Approval, summary, $"approval:{approval.Id:N}"), ct);
            return DecisionResult.Ok;
        }
    }

    public ResumeRun Resume(Guid runId, string? waitKey) => new(runId, waitKey, tenant.TenantId!.Value, tenant.TenantIdentifier!);

    /// <summary>
    /// Stops a run and cancels its pending approvals. Retries when the run was changed concurrently (the
    /// interpreter may be saving it); messages for the run that arrive later find it cancelled and do nothing.
    /// </summary>
    public async Task CancelAsync(AutomationRun run, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            run.Status = RunStatus.Cancelled;
            run.WaitingFor = null;
            run.ResumeAt = null;
            run.CompletedAt = time.GetUtcNow();
            foreach (var approval in await db.Approvals.Where(a => a.RunId == run.Id && a.Status == ApprovalStatus.Pending).ToListAsync(ct))
            {
                approval.Status = ApprovalStatus.Cancelled;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
                run = await db.Runs.FirstAsync(r => r.Id == run.Id, ct);
                if (run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>
/// Every minute:
/// <list type="bullet">
/// <item>resumes runs whose delay is over;</item>
/// <item>recovers runs that stopped making progress: running runs whose handler died (lease expired) or whose
/// message was lost or dead-lettered, and runs waiting for an approval that was decided but not acted on;</item>
/// <item>escalates overdue approvals (adds <c>escalateTo</c> as assignees and notifies everyone).</item>
/// </list>
/// A run it resumes is not looked at again for <see cref="Recheck"/>, so slow or failing runs are not flooded with
/// messages; the interpreter's attempt limit ends runs that never make progress.
/// </summary>
internal sealed class AutomationTimerJob(AutomationDbContext db, ApprovalService approvals, IOutbox outbox, ITenantContext tenant, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "automation.timers";
    public const string Schedule = "* * * * *";
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(5);

    /// <summary>How long a running run may go without progress (and without a lease) before it is resumed.</summary>
    public static readonly TimeSpan Stalled = TimeSpan.FromMinutes(2);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var stalled = now - Stalled;
        var due = await db.Runs.AsNoTracking()
            .Where(r => r.NextCheckAt == null || r.NextCheckAt <= now)
            .Where(r => (r.Status == RunStatus.Waiting && r.ResumeAt != null && r.ResumeAt <= now)
                || (r.Status == RunStatus.Running && r.LastActivityAt < stalled && (r.LeaseUntil == null || r.LeaseUntil < now))
                || (r.Status == RunStatus.Waiting && r.ResumeAt == null && r.LastActivityAt < stalled
                    && db.Approvals.Any(a => a.RunId == r.Id && (a.Status == ApprovalStatus.Approved || a.Status == ApprovalStatus.Rejected)
                        && a.DecidedAt > r.LastActivityAt && a.DecidedAt < stalled)))
            .OrderBy(r => r.LastActivityAt)
            .Select(r => new { r.Id, r.WaitingFor })
            .Take(500)
            .ToListAsync(cancellationToken);
        if (due.Count > 0)
        {
            await outbox.SaveChangesAsync(db, [], [.. due.Select(r => approvals.Resume(r.Id, r.WaitingFor))], cancellationToken);
            var ids = due.Select(r => r.Id).ToList();
            await db.Runs.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(u => u.SetProperty(r => r.NextCheckAt, now + Recheck), cancellationToken);
        }

        var overdue = await db.Approvals.Where(a => a.Status == ApprovalStatus.Pending && !a.Escalated && a.DueAt != null && a.DueAt <= now)
            .Take(100).ToListAsync(cancellationToken);
        foreach (var approval in overdue)
        {
            approval.Escalated = true;
            var added = approval.EscalateTo.Except(approval.Assignees).ToList();
            approval.Assignees = [.. approval.Assignees, .. added];
            try
            {
                await outbox.SaveChangesAsync(db, [], [new NotifyApproval(approval.Id, true, tenant.TenantId!.Value, tenant.TenantIdentifier!)], cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Decided (or changed) meanwhile: a still pending request is escalated next minute.
                db.ChangeTracker.Clear();
            }
        }
    }
}

/// <summary>Options of the automation module (<c>Automation</c> section).</summary>
public sealed class AutomationOptions
{
    /// <summary>Finished runs (and their approvals) are deleted after this many days.</summary>
    public int RunRetentionDays { get; set; } = 30;
}

/// <summary>Daily: deletes finished runs older than <see cref="AutomationOptions.RunRetentionDays"/> with their approvals.</summary>
internal sealed class AutomationRunCleanupJob(AutomationDbContext db, IOptions<AutomationOptions> options, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "automation.runCleanup";
    public const string Schedule = "17 3 * * *";
    private const int Batch = 500;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-Math.Max(1, options.Value.RunRetentionDays));
        while (true)
        {
            var ids = await db.Runs
                .Where(r => (r.Status == RunStatus.Completed || r.Status == RunStatus.Failed || r.Status == RunStatus.Cancelled) && r.CompletedAt < cutoff)
                .Select(r => r.Id).Take(Batch).ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                return;
            }

            await db.Approvals.Where(a => ids.Contains(a.RunId)).ExecuteDeleteAsync(cancellationToken);
            await db.Runs.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
