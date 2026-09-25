using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Automation.Data;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Notifications.Contracts;

namespace PaperDotNet.Automation.Features;

/// <summary>
/// Resumes a workflow run (ADR-0019): sent with the run when it starts, with a decision when an approval is
/// decided, and by the minute job when a delay is over. <see cref="WaitKey"/> names the wait it ends (null for
/// the start); messages for a wait the run no longer has are ignored, so duplicates and stale ones are harmless.
/// </summary>
public sealed record ResumeRun(Guid RunId, string? WaitKey, Guid TenantId, string TenantIdentifier, Guid? UserId = null) : ITenantMessage;

/// <summary>Wolverine handler for <see cref="ResumeRun"/> (discovered by convention).</summary>
public static class ResumeRunHandler
{
    public static async Task Handle(ResumeRun message, ITenantScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateScope(message.TenantId, message.TenantIdentifier);
        await scope.ServiceProvider.GetRequiredService<WorkflowInterpreter>().RunAsync(message.RunId, message.WaitKey, cancellationToken);
    }
}

/// <summary>Starts runs of a workspace's workflows on items.</summary>
internal sealed class WorkflowStarter(AutomationDbContext db, IOutbox outbox, EventCausation causation, ITenantContext tenant, TimeProvider time)
{
    public async Task<(WorkflowRun? Run, string? Error)> StartAsync(AutomationItem item, string workflowName, Guid? startedBy, CancellationToken ct)
    {
        var definition = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.WorkspaceId == item.WorkspaceId && w.Name == workflowName, ct);
        if (definition is null)
        {
            return (null, $"The workflow '{workflowName}' does not exist in the workspace.");
        }

        if (!definition.Enabled)
        {
            return (null, $"The workflow '{workflowName}' is disabled.");
        }

        var run = new WorkflowRun
        {
            Id = Ids.New(),
            DefinitionId = definition.Id,
            DefinitionVersion = definition.CurrentVersion,
            WorkspaceId = item.WorkspaceId,
            ListId = item.ListId,
            ItemId = item.ItemId,
            Status = RunStatus.Running,
            Depth = causation.Depth,
            StartedBy = startedBy,
            StartedAt = time.GetUtcNow(),
        };
        db.Runs.Add(run);

        // The run and the message that starts it are stored together (transactional outbox).
        await outbox.SaveChangesAsync(db, [], [new ResumeRun(run.Id, null, tenant.TenantId!.Value, tenant.TenantIdentifier!)], ct);
        return (run, null);
    }
}

/// <summary>
/// Runs a workflow's compiled steps from the run's position until it finishes or has to wait (EVT-08).
/// Progress is saved after every step, so a retried execution continues where it stopped.
/// </summary>
internal sealed partial class WorkflowInterpreter(
    AutomationDbContext db,
    ActionExecutor executor,
    IListItemStore items,
    RecipientResolver recipients,
    TokenExpander tokens,
    INotificationSender notifications,
    EventCausation causation,
    TimeProvider time,
    ILogger<WorkflowInterpreter> logger)
{
    private const int MaxInstructionsPerRun = 500;
    private const int MaxLogEntries = 100;

    /// <summary>Continues the run when <paramref name="waitKey"/> is the wait it is in (null: not started or running).</summary>
    public async Task RunAsync(Guid runId, string? waitKey, CancellationToken ct)
    {
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled || run.WaitingFor != waitKey)
        {
            return;
        }

        if (run.ResumeAt is { } resumeAt && resumeAt > time.GetUtcNow())
        {
            return;
        }

        causation.Depth = run.Depth + 1;
        var definition = await db.Workflows.AsNoTracking().FirstAsync(w => w.Id == run.DefinitionId, ct);
        var version = await db.WorkflowVersions.AsNoTracking().FirstAsync(v => v.DefinitionId == run.DefinitionId && v.Number == run.DefinitionVersion, ct);
        var program = Definitions.Compile(DefinitionJson.Deserialize<WorkflowSteps>(version.Definition));
        var outcomes = DefinitionJson.Deserialize<Dictionary<string, string>>(run.Outcomes);
        var log = JsonNode.Parse(run.Log) as JsonArray ?? [];
        var item = new AutomationItem(run.WorkspaceId, run.ListId, run.ItemId);
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

        async Task FailAsync(string error)
        {
            Log($"Failed: {error}");
            run.Status = RunStatus.Failed;
            run.Error = RuleRunner.Truncate(error);
            run.WaitingFor = null;
            run.ResumeAt = null;
            run.CompletedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            LogRunFailed(run.Id, error);
        }

        if (run.Status == RunStatus.Waiting && run.Position < program.Count)
        {
            var waiting = program[run.Position];
            if (waiting.Op == OpCode.Approval)
            {
                var decided = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.RunId == run.Id && a.StepName == waiting.StepName && a.Status != ApprovalStatus.Pending && a.Status != ApprovalStatus.Cancelled, ct);
                if (decided is null)
                {
                    return;
                }

                var outcome = decided.Status == ApprovalStatus.Approved ? ApprovalOutcomes.Approved : ApprovalOutcomes.Rejected;
                outcomes[waiting.StepName] = outcome;
                Log($"{waiting.StepName}: {outcome}");
            }

            run.Position++;
            run.Status = RunStatus.Running;
            run.WaitingFor = null;
            run.ResumeAt = null;
            await db.SaveChangesAsync(ct);
        }

        for (var executed = 0; run.Position < program.Count; executed++)
        {
            if (executed >= MaxInstructionsPerRun)
            {
                await FailAsync("The workflow ran too many steps.");
                return;
            }

            var instruction = program[run.Position];
            var step = instruction.Step;
            switch (instruction.Op)
            {
                case OpCode.Action:
                    var result = await executor.ExecuteAsync(
                        new ActionDefinition(step.Action!, step.Inputs), run.WorkspaceId, item, run.StartedBy, null, outcomes,
                        $"workflow:{definition.Name}", $"run:{run.Id:N}:{run.Position}", ct);
                    if (!result.Succeeded)
                    {
                        await FailAsync($"{instruction.StepName} ({step.Action}): {result.Error}");
                        return;
                    }

                    Log($"{instruction.StepName}: {step.Action} done");
                    run.Position++;
                    break;
                case OpCode.Approval:
                    var approval = await CreateApprovalAsync(run, instruction, outcomes, ct);
                    if (approval is null)
                    {
                        await FailAsync($"{instruction.StepName}: no assignee could be found.");
                        return;
                    }

                    run.Status = RunStatus.Waiting;
                    run.WaitingFor = WaitKey(approval.Id);
                    Log($"{instruction.StepName}: waiting for approval");
                    await db.SaveChangesAsync(ct);
                    return;
                case OpCode.Delay:
                    run.Status = RunStatus.Waiting;
                    run.WaitingFor = DelayKey(run.Id, run.Position);
                    run.ResumeAt = time.GetUtcNow().AddHours(step.Hours!.Value);
                    Log($"{instruction.StepName}: waiting {step.Hours} hours");
                    await db.SaveChangesAsync(ct);
                    return;
                case OpCode.Branch:
                    var (holds, error) = await EvaluateAsync(step, item, outcomes, ct);
                    if (error is not null)
                    {
                        await FailAsync($"{instruction.StepName}: {error}");
                        return;
                    }

                    run.Position = holds ? run.Position + 1 : instruction.Target;
                    break;
                case OpCode.Jump:
                    run.Position = instruction.Target;
                    break;
            }

            await db.SaveChangesAsync(ct);
        }

        run.Status = RunStatus.Completed;
        run.CompletedAt = time.GetUtcNow();
        Log("Completed");
        await db.SaveChangesAsync(ct);
    }

    public static string WaitKey(Guid approvalId) => $"approval:{approvalId:N}";

    public static string DelayKey(Guid runId, int position) => $"delay:{runId:N}:{position}";

    private async Task<(bool Holds, string? Error)> EvaluateAsync(WorkflowStep step, AutomationItem item, Dictionary<string, string> outcomes, CancellationToken ct)
    {
        if (step.Step is { } name)
        {
            return (outcomes.TryGetValue(name, out var outcome) && outcome == step.Is, null);
        }

        var (matches, error) = await items.AsSystem().QueryAsync(item.WorkspaceId, item.ListId, new ListItemQuery($"id eq {item.ItemId} and ({step.Filter})", Top: 1), ct);
        return (matches.Count > 0, error);
    }

    /// <summary>The pending request of this step (reused when the step runs again), or a new one with its assignees notified.</summary>
    private async Task<ApprovalRequest?> CreateApprovalAsync(WorkflowRun run, Instruction instruction, Dictionary<string, string> outcomes, CancellationToken ct)
    {
        var existing = await db.Approvals.FirstOrDefaultAsync(a => a.RunId == run.Id && a.StepName == instruction.StepName && a.Status == ApprovalStatus.Pending, ct);
        if (existing is not null)
        {
            return existing;
        }

        var step = instruction.Step;
        var current = await items.AsSystem().GetAsync(run.WorkspaceId, run.ListId, run.ItemId, ct);
        var (assignees, _) = await recipients.ResolveAsync(step.Assignees ?? [], current, run.StartedBy, ct);
        if (assignees.Count == 0)
        {
            return null;
        }

        var (escalateTo, _) = await recipients.ResolveAsync(step.EscalateTo ?? [], current, run.StartedBy, ct);
        var list = await items.AsSystem().GetListAsync(run.WorkspaceId, run.ListId, ct);
        var scope = new TokenScope(current, list?.Name, outcomes, null);
        var title = await tokens.ExpandAsync(step.Title ?? $"Approve {{title}} ({instruction.StepName})", scope, ct);
        var now = time.GetUtcNow();
        var approval = new ApprovalRequest
        {
            Id = Ids.New(),
            RunId = run.Id,
            StepName = instruction.StepName,
            WorkspaceId = run.WorkspaceId,
            ListId = run.ListId,
            ItemId = run.ItemId,
            Title = title.Length > 1000 ? title[..1000] : title,
            Assignees = assignees,
            EscalateTo = [.. escalateTo.Except(assignees)],
            DueAt = step.DueInHours is { } hours ? now.AddHours(hours) : null,
            Status = ApprovalStatus.Pending,
        };
        db.Approvals.Add(approval);
        await db.SaveChangesAsync(ct);
        await notifications.SendAsync(
            new NotificationMessage(NotificationTypes.Automation, title, "An approval is waiting for your decision.",
                new NotificationLink(run.WorkspaceId, run.ListId, run.ItemId), $"approval:{approval.Id:N}"),
            assignees, ct);
        return approval;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId} failed: {Error}")]
    private partial void LogRunFailed(Guid runId, string error);
}

/// <summary>Decisions on approvals and cancelling runs.</summary>
internal sealed class ApprovalService(AutomationDbContext db, IOutbox outbox, ITenantContext tenant, TimeProvider time)
{
    public enum DecisionResult
    {
        Ok,
        NotFound,
        AlreadyDecided,
    }

    /// <summary>Records the decision of an assignee; the message that resumes the run is stored with it.</summary>
    public async Task<DecisionResult> DecideAsync(Guid approvalId, Guid userId, string outcome, string? comment, CancellationToken ct)
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
            await outbox.SaveChangesAsync(db, [], [Resume(approval.RunId, WorkflowInterpreter.WaitKey(approval.Id))], ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DecisionResult.AlreadyDecided;
        }

        return DecisionResult.Ok;
    }

    public ResumeRun Resume(Guid runId, string? waitKey) => new(runId, waitKey, tenant.TenantId!.Value, tenant.TenantIdentifier!);

    /// <summary>
    /// Stops a run and cancels its pending approvals. Retries when the run was changed concurrently (the
    /// interpreter may be saving it); messages for the run that arrive later find it cancelled and do nothing.
    /// </summary>
    public async Task CancelAsync(WorkflowRun run, CancellationToken ct)
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
/// Every minute: resumes runs whose delay is over, and escalates overdue approvals (adds <c>escalateTo</c> as
/// assignees and notifies them and the original assignees).
/// </summary>
internal sealed class AutomationTimerJob(AutomationDbContext db, ApprovalService approvals, IOutbox outbox, INotificationSender notifications, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "automation.timers";
    public const string Schedule = "* * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = await db.Runs.AsNoTracking().Where(r => r.Status == RunStatus.Waiting && r.ResumeAt != null && r.ResumeAt <= now)
            .Select(r => new { r.Id, r.WaitingFor }).Take(500).ToListAsync(cancellationToken);
        if (due.Count > 0)
        {
            await outbox.SaveChangesAsync(db, [], [.. due.Select(r => approvals.Resume(r.Id, r.WaitingFor))], cancellationToken);
        }

        var overdue = await db.Approvals.Where(a => a.Status == ApprovalStatus.Pending && !a.Escalated && a.DueAt != null && a.DueAt <= now)
            .Take(100).ToListAsync(cancellationToken);
        foreach (var approval in overdue)
        {
            approval.Escalated = true;
            var added = approval.EscalateTo.Except(approval.Assignees).ToList();
            approval.Assignees = [.. approval.Assignees, .. added];
            await db.SaveChangesAsync(cancellationToken);
            var link = new NotificationLink(approval.WorkspaceId, approval.ListId, approval.ItemId);
            await notifications.SendAsync(
                new NotificationMessage(NotificationTypes.Automation, $"Overdue: {approval.Title}", "The approval is overdue.", link, $"approval:{approval.Id:N}:overdue"),
                approval.Assignees, cancellationToken);
        }
    }
}
