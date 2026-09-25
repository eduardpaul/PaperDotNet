using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Automation.Data;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace PaperDotNet.Automation.Features;

/// <summary>
/// State kept by WorkflowCore for one run: only what the engine needs to wait and resume. Everything
/// else lives in the tenant-owned <see cref="WorkflowRun"/>.
/// </summary>
public sealed class AutomationRunState
{
    public Guid TenantId { get; set; }

    public string TenantIdentifier { get; set; } = string.Empty;

    public Guid RunId { get; set; }

    public bool Finished { get; set; }

    /// <summary>Event key to wait for (an approval decision).</summary>
    public string? WaitEvent { get; set; }

    /// <summary>Events published from this moment on count (a decision can come before the engine subscribes).</summary>
    public DateTime WaitSince { get; set; }

    /// <summary>Time to wait until (UTC).</summary>
    public DateTime? WaitUntil { get; set; }

    public string? EventData { get; set; }
}

/// <summary>
/// The one WorkflowCore workflow behind every automation workflow (ADR-0018): the interpreter runs the
/// definition's steps until it has to wait; the engine then waits durably for the approval event or the
/// timer and calls the interpreter again. Definitions stay in our tables (versioned, tenant-owned).
/// </summary>
public sealed class AutomationWorkflow : IWorkflow<AutomationRunState>
{
    public const string WorkflowId = "paperdotnet.automation";
    public const string EventName = "paperdotnet.automation";

    public string Id => WorkflowId;

    public int Version => 1;

    public void Build(IWorkflowBuilder<AutomationRunState> builder) => builder
        .StartWith<InterpretStep>()
        .While(d => !d.Finished)
        .Do(loop => loop
            .StartWith(_ => ExecutionResult.Next())
            .If(d => d.WaitEvent != null)
            .Do(wait => wait
                .StartWith(_ => ExecutionResult.Next())
                .WaitFor(EventName, (d, _) => d.WaitEvent!, d => d.WaitSince)
                .Output(d => d.EventData, s => s.EventData))
            .If(d => d.WaitUntil != null)
            .Do(wait => wait
                .StartWith(_ => ExecutionResult.Next())
                .Delay(d => d.WaitUntil!.Value > DateTime.UtcNow ? d.WaitUntil.Value - DateTime.UtcNow : TimeSpan.Zero))
            .Then<InterpretStep>());
}

/// <summary>Runs the interpreter inside the run's tenant.</summary>
public sealed class InterpretStep(ITenantScopeFactory scopes) : StepBodyAsync
{
    public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
    {
        var state = (AutomationRunState)context.Workflow.Data;
        await using var scope = scopes.CreateScope(state.TenantId, state.TenantIdentifier);
        var result = await scope.ServiceProvider.GetRequiredService<WorkflowInterpreter>()
            .RunAsync(state.RunId, context.Workflow.Id, state.EventData, context.CancellationToken);
        state.EventData = null;
        state.Finished = result.Finished;
        state.WaitEvent = result.WaitEvent;
        state.WaitSince = result.WaitSince;
        state.WaitUntil = result.WaitUntil;
        return ExecutionResult.Next();
    }
}

internal sealed record InterpretResult(bool Finished, string? WaitEvent = null, DateTime WaitSince = default, DateTime? WaitUntil = null)
{
    public static readonly InterpretResult Done = new(true);
}

/// <summary>Starts runs of a workspace's workflows on items.</summary>
internal sealed class WorkflowStarter(AutomationDbContext db, IWorkflowController engine, EventCausation causation, ITenantContext tenant, TimeProvider time)
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
        await db.SaveChangesAsync(ct);

        // The engine may run the first step at once; that step records the engine id (no second write here).
        await engine.StartWorkflow(AutomationWorkflow.WorkflowId, 1,
            new AutomationRunState { TenantId = tenant.TenantId!.Value, TenantIdentifier = tenant.TenantIdentifier!, RunId = run.Id }, run.Id.ToString());
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

    public async Task<InterpretResult> RunAsync(Guid runId, string engineId, string? eventData, CancellationToken ct)
    {
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
        {
            return InterpretResult.Done;
        }

        run.EngineId ??= engineId;

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

        async Task<InterpretResult> FailAsync(string error)
        {
            Log($"Failed: {error}");
            run.Status = RunStatus.Failed;
            run.Error = RuleRunner.Truncate(error);
            run.WaitingFor = null;
            run.CompletedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            LogRunFailed(run.Id, error);
            return InterpretResult.Done;
        }

        if (run.Status == RunStatus.Waiting && run.Position < program.Count)
        {
            var waiting = program[run.Position];
            if (waiting.Op == OpCode.Approval)
            {
                if (eventData is not (ApprovalOutcomes.Approved or ApprovalOutcomes.Rejected))
                {
                    return new InterpretResult(false, run.WaitingFor, time.GetUtcNow().UtcDateTime.AddMinutes(-5));
                }

                outcomes[waiting.StepName] = eventData;
                Log($"{waiting.StepName}: {eventData}");
            }

            run.Position++;
            run.Status = RunStatus.Running;
            run.WaitingFor = null;
            await db.SaveChangesAsync(ct);
        }

        for (var executed = 0; run.Position < program.Count; executed++)
        {
            if (executed >= MaxInstructionsPerRun)
            {
                return await FailAsync("The workflow ran too many steps.");
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
                        return await FailAsync($"{instruction.StepName} ({step.Action}): {result.Error}");
                    }

                    Log($"{instruction.StepName}: {step.Action} done");
                    run.Position++;
                    break;
                case OpCode.Approval:
                    var approval = await CreateApprovalAsync(run, instruction, outcomes, ct);
                    if (approval is null)
                    {
                        return await FailAsync($"{instruction.StepName}: no assignee could be found.");
                    }

                    run.Status = RunStatus.Waiting;
                    run.WaitingFor = WaitKey(approval.Id);
                    Log($"{instruction.StepName}: waiting for approval");
                    await db.SaveChangesAsync(ct);
                    return new InterpretResult(false, run.WaitingFor, approval.CreatedAt.UtcDateTime.AddMinutes(-5));
                case OpCode.Delay:
                    run.Status = RunStatus.Waiting;
                    Log($"{instruction.StepName}: waiting {step.Hours} hours");
                    await db.SaveChangesAsync(ct);
                    return new InterpretResult(false, WaitUntil: time.GetUtcNow().UtcDateTime.AddHours(step.Hours!.Value));
                case OpCode.Branch:
                    var (holds, error) = await EvaluateAsync(step, item, outcomes, ct);
                    if (error is not null)
                    {
                        return await FailAsync($"{instruction.StepName}: {error}");
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
        return InterpretResult.Done;
    }

    public static string WaitKey(Guid approvalId) => $"approval:{approvalId:N}";

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
internal sealed class ApprovalService(AutomationDbContext db, IWorkflowController engine, TimeProvider time)
{
    public enum DecisionResult
    {
        Ok,
        NotFound,
        AlreadyDecided,
    }

    /// <summary>Records the decision of an assignee and resumes the run.</summary>
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
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DecisionResult.AlreadyDecided;
        }

        await PublishAsync(approval);
        return DecisionResult.Ok;
    }

    public Task PublishAsync(ApprovalRequest approval) =>
        engine.PublishEvent(AutomationWorkflow.EventName, WorkflowInterpreter.WaitKey(approval.Id),
            approval.Status == ApprovalStatus.Approved ? ApprovalOutcomes.Approved : ApprovalOutcomes.Rejected);

    /// <summary>
    /// Stops a run: its engine instance ends and pending approvals are cancelled. Retries when the run was
    /// changed concurrently (the interpreter may be saving it); an interpreter that runs later sees the status.
    /// </summary>
    public async Task CancelAsync(WorkflowRun run, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            run.Status = RunStatus.Cancelled;
            run.WaitingFor = null;
            run.CompletedAt = time.GetUtcNow();
            foreach (var approval in await db.Approvals.Where(a => a.RunId == run.Id && a.Status == ApprovalStatus.Pending).ToListAsync(ct))
            {
                approval.Status = ApprovalStatus.Cancelled;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                break;
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

        if (run.EngineId is { } engineId)
        {
            await engine.TerminateWorkflow(engineId);
        }
    }
}

/// <summary>
/// Every minute: escalates overdue approvals (adds <c>escalateTo</c> as assignees and notifies them and the
/// original assignees) and publishes decisions again whose run still waits (the event was lost, e.g. in a crash).
/// </summary>
internal sealed class ApprovalEscalationJob(AutomationDbContext db, ApprovalService approvals, INotificationSender notifications, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "automation.approvals";
    public const string Schedule = "* * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
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

        var stale = now.AddMinutes(-1);
        var decided = await db.Approvals
            .Where(a => (a.Status == ApprovalStatus.Approved || a.Status == ApprovalStatus.Rejected) && a.DecidedAt != null && a.DecidedAt < stale)
            .Join(db.Runs.Where(r => r.Status == RunStatus.Waiting), a => a.RunId, r => r.Id, (a, r) => new { Approval = a, r.WaitingFor })
            .Take(100).ToListAsync(cancellationToken);
        foreach (var entry in decided.Where(e => e.WaitingFor == WorkflowInterpreter.WaitKey(e.Approval.Id)))
        {
            await approvals.PublishAsync(entry.Approval);
        }
    }
}
