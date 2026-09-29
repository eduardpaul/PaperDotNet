using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Resumes a workflow run (ADR-0019, ADR-0036): sent with the run when it starts, with the completion of the bookmark it
/// waits on (an approval decided, a delay over), and by the minute job to recover stalled runs. <see cref="BookmarkId"/>
/// names the wait it ends (null for the start or a running run); messages for a wait the run is not in are ignored, so
/// duplicates and stale ones are harmless.
/// </summary>
public sealed record ResumeRun(Guid RunId, Guid? BookmarkId, Guid TenantId, string TenantIdentifier, Guid? UserId = null) : ITenantMessage;

/// <summary>Wolverine handler for <see cref="ResumeRun"/> (discovered by convention).</summary>
public static class ResumeRunHandler
{
    public static async Task Handle(ResumeRun message, ITenantScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateScope(message.TenantId, message.TenantIdentifier);
        await scope.ServiceProvider.GetRequiredService<WorkflowInterpreter>().RunAsync(message.RunId, message.BookmarkId, cancellationToken);
    }
}

/// <summary>A run to start; with an <see cref="Error"/> (e.g. a condition that cannot be checked) it is saved as failed.</summary>
internal sealed record WorkflowStart(
    WorkflowDefinition Workflow, WorkflowItem? Item, Guid? EventId, string? Data, int Depth, Guid? StartedBy, string? Error = null,
    JsonObject? Variables = null);

/// <summary>Starts runs of a workspace's workflows.</summary>
internal sealed class WorkflowStarter(
    WorkflowsDbContext db, IListItemStore items, IOutbox outbox, EventCausation causation, ITenantContext tenant, TimeProvider time)
{
    /// <summary>Whether the item matches an OData condition; an error when the condition cannot be checked.</summary>
    public async Task<(bool Matches, string? Error)> CheckConditionAsync(WorkflowItem item, string condition, CancellationToken ct)
    {
        var (matches, error) = await items.AsSystem().QueryAsync(
            item.WorkspaceId, item.ListId, new ListItemQuery($"id eq {item.ItemId} and ({condition})", Top: 1), ct);
        return (error is null && matches.Count > 0, error is null ? null : $"condition: {error}");
    }

    /// <summary>Saves the runs together with the messages that start them (transactional outbox).</summary>
    public async Task<List<WorkflowRun>> StartAsync(IReadOnlyList<WorkflowStart> starts, CancellationToken ct)
    {
        if (starts.Count == 0)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var runs = new List<WorkflowRun>();
        var messages = new List<ITenantMessage>();
        foreach (var start in starts)
        {
            var run = new WorkflowRun
            {
                Id = Ids.New(),
                WorkflowId = start.Workflow.Id,
                WorkflowVersion = start.Workflow.CurrentVersion,
                WorkspaceId = start.Workflow.WorkspaceId,
                ListId = start.Item?.ListId,
                ItemId = start.Item?.ItemId,
                EventId = start.EventId,
                Data = start.Data,
                Variables = start.Variables?.ToJsonString() ?? "{}",
                Status = RunStatus.Running,
                Depth = start.Depth,
                StartedBy = start.StartedBy,
                StartedAt = now,
                LastActivityAt = now,
            };
            if (start.Error is { } error)
            {
                run.Status = RunStatus.Failed;
                run.Error = WorkflowInterpreter.Truncate(error);
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

    /// <summary>Most items one manual start covers.</summary>
    public const int MaxItemsPerStart = 100;

    /// <summary>
    /// Starts a workflow with the <c>manual</c> trigger: once per item, or once without an item when there are none
    /// (only for triggers without a list). Every item and the inputs are checked before anything starts; the inputs
    /// become the runs' variables.
    /// </summary>
    public async Task<(List<WorkflowRun> Runs, string? Error)> StartManualAsync(
        WorkflowDefinition workflow, IReadOnlyList<WorkflowItem> targets, JsonObject? inputs, Guid? startedBy, CancellationToken ct)
    {
        var name = workflow.Name;
        if (workflow.Trigger != WorkflowTriggers.Manual)
        {
            return ([], $"The workflow '{name}' is not started manually (its trigger is {workflow.Trigger}).");
        }

        if (!workflow.Enabled)
        {
            return ([], $"The workflow '{name}' is disabled.");
        }

        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == workflow.Id && v.Number == workflow.CurrentVersion, ct);
        var spec = DefinitionJson.Deserialize<WorkflowSpec>(version.Definition);
        if (WorkflowInputs.Check(spec.Trigger.Inputs, inputs) is { } invalid)
        {
            return ([], invalid);
        }

        if (targets.Count == 0 && spec.Trigger.List is { } needed)
        {
            return ([], $"The workflow '{name}' runs on items of the list '{needed}'; choose items.");
        }

        var store = items.AsSystem();
        foreach (var item in targets)
        {
            var list = await store.GetListAsync(item.WorkspaceId, item.ListId, ct);
            if (spec.Trigger.List is { } listName && list?.Name != listName)
            {
                return ([], $"The workflow '{name}' only runs on items of the list '{listName}'.");
            }

            if (spec.Trigger.ContentType is { } type)
            {
                var data = await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, ct);
                var contentType = list?.ContentTypes.FirstOrDefault(c => c.Id == data?.ContentTypeId);
                if (contentType?.Name != type && contentType?.Key != type)
                {
                    return ([], $"The workflow '{name}' only runs on items of the content type '{type}'.");
                }
            }

            if (spec.Condition is { } condition)
            {
                var (matches, error) = await CheckConditionAsync(item, condition, ct);
                if (!matches)
                {
                    return ([], error ?? "The item does not match the workflow's condition.");
                }
            }
        }

        var each = targets.Count == 0 ? [null] : targets.Select(t => (WorkflowItem?)t).ToList();
        var runs = await StartAsync([.. each.Select(item => new WorkflowStart(workflow, item, null, null, causation.Depth, startedBy, Variables: inputs))], ct);
        return (runs, null);
    }
}

/// <summary>
/// Inputs of manual starts: the trigger's <c>inputs</c> describe them as a JSON Schema object (<c>properties</c> with a
/// <c>type</c> each, <c>required</c>); a start's values are checked against it and become run variables.
/// </summary>
internal static class WorkflowInputs
{
    private static readonly string[] Types = ["string", "number", "integer", "boolean", "array", "object"];

    /// <summary>Checks the schema itself when a workflow is saved.</summary>
    public static IEnumerable<string> ValidateSchema(JsonObject? schema)
    {
        if (schema is null)
        {
            yield break;
        }

        if (schema["properties"] is not JsonObject properties)
        {
            yield return "inputs needs properties (a JSON Schema object).";
            yield break;
        }

        foreach (var (name, property) in properties)
        {
            if (property?["type"] is not JsonValue type || !Types.Contains(type.ToString()))
            {
                yield return $"inputs.{name} needs a type ({string.Join(", ", Types)}).";
            }
        }

        foreach (var required in schema["required"] as JsonArray ?? [])
        {
            if (required?.ToString() is not { } name || !properties.ContainsKey(name))
            {
                yield return $"inputs.required names '{required}', which is not a property.";
            }
        }
    }

    /// <summary>Why the values do not fit the schema, or null (no schema: any values).</summary>
    public static string? Check(JsonObject? schema, JsonObject? values)
    {
        if (schema?["properties"] is not JsonObject properties)
        {
            return null;
        }

        foreach (var required in schema["required"] as JsonArray ?? [])
        {
            if (required?.ToString() is { } name && values?[name] is null)
            {
                return $"The input '{name}' is required.";
            }
        }

        foreach (var (name, value) in values ?? [])
        {
            if (properties[name]?["type"]?.ToString() is not { } type)
            {
                return $"Unknown input '{name}'.";
            }

            var kind = value?.GetValueKind();
            var fits = type switch
            {
                "string" => kind == System.Text.Json.JsonValueKind.String,
                "number" => kind == System.Text.Json.JsonValueKind.Number,
                "integer" => kind == System.Text.Json.JsonValueKind.Number && value!.ToJsonString().All(c => char.IsDigit(c) || c == '-'),
                "boolean" => kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
                "array" => kind == System.Text.Json.JsonValueKind.Array,
                _ => kind == System.Text.Json.JsonValueKind.Object,
            };
            if (value is not null && !fits)
            {
                return $"The input '{name}' must be of type {type}.";
            }
        }

        return null;
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
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == message.ApprovalId, cancellationToken);
        if (approval is not { Status: ApprovalStatus.Pending })
        {
            return;
        }

        var link = new NotificationLink(approval.WorkspaceId, approval.ListId, approval.ItemId);
        var notification = message.Overdue
            ? new NotificationMessage(NotificationTypes.Workflow, $"Overdue: {approval.Title}", "The approval is overdue.", link, $"approval:{approval.Id:N}:overdue")
            : new NotificationMessage(NotificationTypes.Workflow, approval.Title, "An approval is waiting for your decision.", link, $"approval:{approval.Id:N}");
        await scope.ServiceProvider.GetRequiredService<INotificationSender>().SendAsync(notification, approval.Assignees, cancellationToken);
    }
}

/// <summary>
/// Runs a workflow's flow (ADR-0036) from the run's node until it finishes or has to wait (EVT-07, EVT-08).
/// <list type="bullet">
/// <item>A handler first claims the run with a lease (a conditional update), so only one server executes it at a time;
/// a busy run makes the message retry later. A crashed handler's lease expires and the timer job resumes the run.</item>
/// <item>Progress (node, variables, outputs) is saved after every node, so a retried execution continues where it
/// stopped. An action gets a stable execution id and key before it runs, the same when it runs again.</item>
/// <item>Waits are bookmarks: the run stops and a completed bookmark (with the message that resumes it) continues it
/// with the bookmark's payload as the node's output.</item>
/// <item>A failing node is tried again by its retry policy, else continues on its <c>error</c> port, else fails the run
/// at that node (it can be retried from there).</item>
/// <item>A run that keeps failing to make progress is failed after <see cref="MaxAttempts"/> attempts.</item>
/// </list>
/// </summary>
internal sealed partial class WorkflowInterpreter(
    WorkflowsDbContext db,
    ActionExecutor executor,
    IListItemStore items,
    RecipientResolver recipients,
    TokenExpander tokens,
    IOutbox outbox,
    EventCausation causation,
    ITenantContext tenant,
    TimeProvider time,
    ILogger<WorkflowInterpreter> logger)
{
    public const int MaxAttempts = 10;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private const int MaxNodesPerExecution = 500;
    private const int MaxNodesPerRun = 1000;
    private const int MaxLogEntries = 100;
    private const int MaxStateLength = 256 * 1024;

    /// <summary>Continues the run when <paramref name="bookmarkId"/> is the wait it is in (null: not started or running).</summary>
    public async Task RunAsync(Guid runId, Guid? bookmarkId, CancellationToken ct)
    {
        var current = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (current is null || IsFinished(current.Status) || current.WaitingOn != bookmarkId)
        {
            return;
        }

        var lease = Ids.New();
        var now = time.GetUtcNow();
        var claimed = await db.Runs
            .Where(r => r.Id == runId && r.WaitingOn == bookmarkId && (r.LeaseUntil == null || r.LeaseUntil < now)
                && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseId, lease).SetProperty(r => r.LeaseUntil, now + Lease).SetProperty(r => r.Attempts, r => r.Attempts + 1), ct);
        if (claimed == 0)
        {
            var again = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (again is null || IsFinished(again.Status) || again.WaitingOn != bookmarkId)
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
        var workflow = await db.Workflows.AsNoTracking().FirstAsync(a => a.Id == run.WorkflowId, ct);
        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == run.WorkflowId && v.Number == run.WorkflowVersion, ct);
        var spec = DefinitionJson.Deserialize<WorkflowSpec>(version.Definition);
        var flow = Definitions.FlowOf(spec);
        var outputs = JsonNode.Parse(run.Outputs) as JsonObject ?? [];
        var variables = JsonNode.Parse(run.Variables) as JsonObject ?? [];
        var log = JsonNode.Parse(run.Log) as JsonArray ?? [];
        var item = run.ListId is { } listId && run.ItemId is { } itemId ? new WorkflowItem(run.WorkspaceId, listId, itemId) : null;
        var data = run.Data is { } json ? JsonNode.Parse(json) as JsonObject : null;
        void Log(string message)
        {
            log.Add(new JsonObject { ["at"] = time.GetUtcNow().ToString("O"), ["message"] = message });
            while (log.Count > MaxLogEntries)
            {
                log.RemoveAt(0);
            }
        }

        // Saves progress and extends the lease; releases it when the run stops (finished or waiting).
        Task SaveAsync(IReadOnlyCollection<ITenantMessage>? messages = null)
        {
            var now = time.GetUtcNow();
            run.LastActivityAt = now;
            run.Log = log.ToJsonString();
            run.Outputs = outputs.ToJsonString();
            run.Variables = variables.ToJsonString();
            var stopped = run.Status != RunStatus.Running;
            run.LeaseId = stopped ? null : lease;
            run.LeaseUntil = stopped ? null : now + Lease;
            return messages is { Count: > 0 } ? outbox.SaveChangesAsync(db, [], messages, ct) : db.SaveChangesAsync(ct);
        }

        void Advance(string node)
        {
            run.Node = node;
            run.StepExecutionId = null;
            run.Attempts = 0;
            run.NodeAttempts = 0;
        }

        async Task FailAsync(string error, string? node)
        {
            Log($"Failed: {error}");
            run.Status = RunStatus.Failed;
            run.Error = Truncate(error);
            run.FailedNode = node;
            run.WaitingOn = null;
            run.CompletedAt = time.GetUtcNow();
            await SaveAsync();
            LogRunFailed(run.Id, error);
        }

        async Task CompleteAsync()
        {
            run.Status = RunStatus.Completed;
            run.CompletedAt = time.GetUtcNow();
            Log("Completed");
            await SaveAsync();
        }

        // Moves on along the outcome's port (done when that port is not connected); false when the run ended.
        async Task<bool> ContinueAsync(string node, string outcome)
        {
            var next = flow.Nodes.GetValueOrDefault(node)?.Next;
            var target = next?.GetValueOrDefault(outcome) ?? (outcome == "error" ? null : next?.GetValueOrDefault("done"));
            if (target is not null)
            {
                Advance(target);
                return true;
            }

            await CompleteAsync();
            return false;
        }

        Task WaitAsync(WorkflowBookmark bookmark, IReadOnlyCollection<ITenantMessage>? messages = null)
        {
            run.Status = RunStatus.Waiting;
            run.WaitingOn = bookmark.Id;
            run.NextCheckAt = null;
            return SaveAsync(messages);
        }

        // A failure of a node: its retry policy, else its error port, else the run fails there. False when the run stopped.
        async Task<bool> FailedAsync(string id, FlowNode node, string error)
        {
            if (node.Retry is { } retry && run.NodeAttempts < retry.Attempts)
            {
                run.NodeAttempts++;
                var delay = retry.DelayMinutes ?? 1;
                Log($"{id}: {error} (trying again in {delay} minutes, {run.NodeAttempts} of {retry.Attempts})");
                var bookmark = NewBookmark(run, id, BookmarkKinds.Retry, $"{run.Id:N}:{id}:{run.Executed}", time.GetUtcNow().AddMinutes(delay));
                await WaitAsync(bookmark);
                return false;
            }

            if (node.Next?.GetValueOrDefault("error") is { } target)
            {
                outputs[id] = new JsonObject { ["error"] = error };
                Log($"{id}: {error} (continuing with {target})");
                Advance(target);
                return true;
            }

            await FailAsync($"{id} ({node.Activity}): {error}", id);
            return false;
        }

        TokenScope? scope = null;
        async Task<string> ExpandAsync(string template)
        {
            if (scope is null)
            {
                var store = items.AsSystem();
                var current = item is null ? null : await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, ct);
                var list = item is null ? null : await store.GetListAsync(item.WorkspaceId, item.ListId, ct);
                scope = new TokenScope(current, list?.Name, outputs, variables, data);
            }

            return await tokens.ExpandAsync(template, scope, ct);
        }

        if (run.Attempts > MaxAttempts)
        {
            await FailAsync($"The run stopped after {MaxAttempts} attempts without progress (see the server log).", run.Node);
            return;
        }

        if (run.Node is null)
        {
            run.Node = flow.Start;

            // The definition's initial variables, overridden by the inputs of a manual start.
            var initial = spec.Variables?.DeepClone().AsObject() ?? [];
            foreach (var (name, value) in variables)
            {
                initial[name] = value?.DeepClone();
            }

            variables = initial;
        }

        if (run.Status == RunStatus.Waiting)
        {
            var bookmark = run.WaitingOn is { } waitingOn ? await db.Bookmarks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == waitingOn, ct) : null;
            if (bookmark is null)
            {
                await FailAsync("The wait of the run no longer exists.", run.Node);
                return;
            }

            if (bookmark.CompletedAt is null)
            {
                // Nothing to do yet (e.g. a duplicate message); not a failed attempt.
                run.Attempts = 0;
                await SaveAsync();
                return;
            }

            run.Status = RunStatus.Running;
            run.WaitingOn = null;
            run.NextCheckAt = null;
            if (bookmark.RunAgain)
            {
                // The node runs again (same execution id) and looks up what it waited for itself, e.g. a batch answer.
                Log($"{bookmark.Node}: running again");
            }
            else if (bookmark.Kind != BookmarkKinds.Retry)
            {
                var payload = bookmark.Payload is { } text ? JsonNode.Parse(text) as JsonObject ?? [] : [];
                outputs[bookmark.Node] = payload;
                var outcome = payload["outcome"] is JsonValue value && value.TryGetValue<string>(out var named) ? named : "done";
                if (bookmark.Kind != BookmarkKinds.Delay)
                {
                    Log($"{bookmark.Node}: {outcome}");
                }

                if (!await ContinueAsync(bookmark.Node, outcome))
                {
                    return;
                }
            }

            await SaveAsync();
        }

        for (var executed = 0; ; executed++)
        {
            if (executed >= MaxNodesPerExecution || run.Executed >= MaxNodesPerRun)
            {
                await FailAsync("The workflow ran too many steps.", run.Node);
                return;
            }

            scope = null; // the item may have changed
            var id = run.Node!;
            if (!flow.Nodes.TryGetValue(id, out var node))
            {
                await FailAsync($"The node '{id}' does not exist.", null);
                return;
            }

            run.Executed++;
            var inputs = node.Inputs ?? [];
            switch (node.Activity)
            {
                case FlowActivities.End:
                    await CompleteAsync();
                    return;
                case FlowActivities.Fail:
                    await FailAsync(await ExpandAsync(Inputs.Text(inputs, "message") ?? "The workflow ended with a failure."), null);
                    return;
                case FlowActivities.SetVariable:
                    var name = Inputs.Text(inputs, "name")!;
                    variables[name] = inputs["value"] is JsonValue template && template.TryGetValue<string>(out var text)
                        ? JsonValue.Create(await ExpandAsync(text))
                        : inputs["value"]?.DeepClone();
                    if (variables.ToJsonString().Length > MaxStateLength)
                    {
                        await FailAsync($"{id}: the variables are too large.", id);
                        return;
                    }

                    if (!await ContinueAsync(id, "done"))
                    {
                        return;
                    }

                    break;
                case FlowActivities.If:
                    var (holds, error) = await EvaluateAsync(inputs, item, outputs, ExpandAsync, ct);
                    if (error is not null ? !await FailedAsync(id, node, error) : !await ContinueAsync(id, holds ? "true" : "false"))
                    {
                        return;
                    }

                    break;
                case FlowActivities.Delay:
                    var hours = Inputs.Number(inputs, "hours") ?? 0;
                    Log($"{id}: waiting {hours} hours");
                    await WaitAsync(NewBookmark(run, id, BookmarkKinds.Delay, $"{run.Id:N}:{id}:{run.Executed}", time.GetUtcNow().AddHours(hours)));
                    return;
                case FlowActivities.Approval:
                    if (item is null)
                    {
                        await FailAsync($"{id}: an approval needs an item (the trigger has none).", id);
                        return;
                    }

                    var approval = await CreateApprovalAsync(run, item, id, inputs, ExpandAsync, ct);
                    if (approval is null)
                    {
                        await FailAsync($"{id}: no assignee could be found.", id);
                        return;
                    }

                    // The request, the wait and the notification are saved together: a decision can never find the run
                    // not yet waiting for it, and the assignees are always told.
                    var key = BookmarkKey(approval.Id);
                    var waitFor = await db.Bookmarks.FirstOrDefaultAsync(b => b.Kind == BookmarkKinds.Approval && b.Key == key, ct)
                        ?? NewBookmark(run, id, BookmarkKinds.Approval, key, null);
                    Log($"{id}: waiting for approval");
                    await WaitAsync(waitFor, [new NotifyApproval(approval.Id, false, tenant.TenantId!.Value, tenant.TenantIdentifier!)]);
                    return;
                default:
                    if (run.StepExecutionId is null)
                    {
                        // Stable across retries of this node: actions use it to be safe to repeat.
                        run.StepExecutionId = Ids.New();
                        await SaveAsync();
                    }

                    var result = await executor.ExecuteAsync(
                        new ActionDefinition(node.Activity, node.Inputs), run.WorkspaceId, item, run.StartedBy, data, outputs, variables,
                        $"workflow:{workflow.Name}", $"run:{run.Id:N}:{run.StepExecutionId.Value:N}", run.StepExecutionId.Value, ct, run.Id);
                    if (!result.Succeeded)
                    {
                        if (!await FailedAsync(id, node, result.Error ?? "The action failed."))
                        {
                            return;
                        }

                        break;
                    }

                    if (result.Waiting is { } wait)
                    {
                        // The activity waits for something else (e.g. a batch) to complete (kind, key): a bookmark of this
                        // node; a completion that came first (unclaimed) is taken over and resumes the run right away.
                        var existing = await db.Bookmarks.FirstOrDefaultAsync(b => b.Kind == wait.Kind && b.Key == wait.Key, ct);
                        var problem = WaitProblem(wait, existing, run, id);
                        if (problem is not null)
                        {
                            if (!await FailedAsync(id, node, problem))
                            {
                                return;
                            }

                            break;
                        }

                        var waitOn = existing ?? NewBookmark(run, id, wait.Kind, wait.Key, wait.ResumeAt);
                        if (wait.RunAgain && existing is { CompletedAt: not null } && existing.RunId == run.Id)
                        {
                            // The node ran again and still waits for the same thing: wait anew.
                            existing.CompletedAt = null;
                            existing.Payload = null;
                            existing.ResumeAt = wait.ResumeAt;
                        }

                        waitOn.RunId = run.Id;
                        waitOn.Node = id;
                        waitOn.RunAgain = wait.RunAgain;
                        Log($"{id}: waiting ({wait.Kind})");
                        await WaitAsync(waitOn, waitOn.CompletedAt is null ? null : [new ResumeRun(run.Id, waitOn.Id, tenant.TenantId!.Value, tenant.TenantIdentifier!)]);
                        return;
                    }

                    outputs[id] = result.Output?.DeepClone() ?? new JsonObject();
                    if (outputs.ToJsonString().Length > MaxStateLength)
                    {
                        await FailAsync($"{id}: the outputs of the run are too large.", id);
                        return;
                    }

                    Log($"{id}: {node.Activity} done" + (result.Outcome is { } chosen ? $" ({chosen})" : string.Empty));
                    if (!await ContinueAsync(id, result.Outcome ?? "done"))
                    {
                        return;
                    }

                    break;
            }

            await SaveAsync();
        }
    }

    public static string BookmarkKey(Guid approvalId) => approvalId.ToString("N");

    /// <summary>Why an activity cannot wait on (kind, key), or null: a valid wait that is new, unclaimed or this node's own.</summary>
    private static string? WaitProblem(WorkflowWait wait, WorkflowBookmark? existing, WorkflowRun run, string node)
    {
        try
        {
            WorkflowBookmarks.CheckWait(wait.Kind, wait.Key);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        return existing is null || existing.RunId == WorkflowBookmarks.Unclaimed || (existing.RunId == run.Id && existing.Node == node)
            ? null
            : $"The wait {wait.Kind} '{wait.Key}' belongs to another run.";
    }

    public static string Truncate(string text) => text.Length > 2000 ? text[..2000] : text;

    private WorkflowBookmark NewBookmark(WorkflowRun run, string node, string kind, string key, DateTimeOffset? resumeAt)
    {
        var bookmark = new WorkflowBookmark { Id = Ids.New(), RunId = run.Id, Node = node, Kind = kind, Key = key, ResumeAt = resumeAt, CreatedAt = time.GetUtcNow() };
        db.Bookmarks.Add(bookmark);
        return bookmark;
    }

    private async Task<(bool Holds, string? Error)> EvaluateAsync(
        JsonObject inputs, WorkflowItem? item, JsonObject outputs, Func<string, Task<string>> expand, CancellationToken ct)
    {
        if (Inputs.Text(inputs, "step") is { } step)
        {
            return (outputs[step]?["outcome"] is JsonValue outcome && outcome.ToString() == Inputs.Text(inputs, "is"), null);
        }

        if (Inputs.Text(inputs, "op") is { } op)
        {
            var left = Inputs.Text(inputs, "left") is { } l ? await expand(l) : string.Empty;
            var right = Inputs.Text(inputs, "right") is { } r ? await expand(r) : string.Empty;
            return (Comparison.Holds(left, op, right), null);
        }

        if (item is null)
        {
            return (false, "a filter needs an item (the trigger has none).");
        }

        var (matches, error) = await items.AsSystem().QueryAsync(item.WorkspaceId, item.ListId, new ListItemQuery($"id eq {item.ItemId} and ({Inputs.Text(inputs, "filter")})", Top: 1), ct);
        return (matches.Count > 0, error);
    }

    /// <summary>The pending request of this node (reused when the node runs again), or a new one (not saved yet).</summary>
    private async Task<ApprovalRequest?> CreateApprovalAsync(
        WorkflowRun run, WorkflowItem item, string node, JsonObject inputs, Func<string, Task<string>> expand, CancellationToken ct)
    {
        var existing = await db.Approvals.FirstOrDefaultAsync(a => a.RunId == run.Id && a.StepName == node && a.Status == ApprovalStatus.Pending, ct);
        if (existing is not null)
        {
            return existing;
        }

        var current = await items.AsSystem().GetAsync(item.WorkspaceId, item.ListId, item.ItemId, ct);
        var (assignees, _) = await recipients.ResolveAsync(Inputs.Texts(inputs, "assignees") ?? [], current, run.StartedBy, ct);
        if (assignees.Count == 0)
        {
            return null;
        }

        var (escalateTo, _) = await recipients.ResolveAsync(Inputs.Texts(inputs, "escalateTo") ?? [], current, run.StartedBy, ct);
        var title = await expand(Inputs.Text(inputs, "title") ?? $"Approve {{title}} ({node})");
        var approval = new ApprovalRequest
        {
            Id = Ids.New(),
            RunId = run.Id,
            StepName = node,
            WorkspaceId = run.WorkspaceId,
            ListId = item.ListId,
            ItemId = item.ItemId,
            Title = title.Length > 1000 ? title[..1000] : title,
            Assignees = assignees,
            EscalateTo = [.. escalateTo.Except(assignees)],
            DueAt = Inputs.Number(inputs, "dueInHours") is { } hours ? time.GetUtcNow().AddHours(hours) : null,
            Status = ApprovalStatus.Pending,
        };
        db.Approvals.Add(approval);
        return approval;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId} failed: {Error}")]
    private partial void LogRunFailed(Guid runId, string error);
}

/// <summary>Decisions on approvals, completing bookmarks, and cancelling and retrying runs.</summary>
internal sealed class RunService(WorkflowsDbContext db, IOutbox outbox, ITenantContext tenant, TimeProvider time, IItemActivity activity)
{
    public enum DecisionResult
    {
        Ok,
        NotFound,
        AlreadyDecided,
    }

    /// <summary>
    /// Records the decision of an assignee and completes the approval's bookmark; the message that resumes the run is
    /// stored with it. A concurrent change of the request (e.g. an escalation adding assignees) is retried.
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
            var messages = new List<ITenantMessage>();
            var key = WorkflowInterpreter.BookmarkKey(approval.Id);
            if (await db.Bookmarks.FirstOrDefaultAsync(b => b.Kind == BookmarkKinds.Approval && b.Key == key && b.CompletedAt == null, ct) is { } bookmark)
            {
                Complete(bookmark, new JsonObject { ["outcome"] = outcome, ["decidedBy"] = userId.ToString(), ["comment"] = comment });
                messages.Add(Resume(bookmark.RunId, bookmark.Id));
            }

            // Workflows can react to the decision (approval.decided), one level deeper than the run that asked.
            var origin = await db.Runs.AsNoTracking().Where(r => r.Id == approval.RunId)
                .Select(r => new { r.Depth, Workflow = db.Workflows.Where(w => w.Id == r.WorkflowId).Select(w => w.Name).FirstOrDefault() })
                .FirstOrDefaultAsync(ct);
            var decided = new WorkflowTriggerRaised
            {
                TenantId = tenant.TenantId!.Value,
                TenantIdentifier = tenant.TenantIdentifier!,
                UserId = userId,
                Depth = (origin?.Depth ?? 0) + 1,
                Trigger = WorkflowTriggers.ApprovalDecided,
                WorkspaceId = approval.WorkspaceId,
                ListId = approval.ListId,
                ItemId = approval.ItemId,
                Data = new JsonObject
                {
                    ["workflow"] = origin?.Workflow,
                    ["step"] = approval.StepName,
                    ["outcome"] = outcome,
                    ["comment"] = comment,
                    ["decidedBy"] = userId.ToString(),
                }.ToJsonString(),
            };

            try
            {
                await outbox.SaveChangesAsync(db, [decided], messages, ct);
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

    /// <summary>Marks a tracked bookmark completed with its payload (the caller saves it with the resume message).</summary>
    public void Complete(WorkflowBookmark bookmark, JsonObject? payload)
    {
        bookmark.CompletedAt = time.GetUtcNow();
        bookmark.Payload = payload?.ToJsonString();
    }

    public ResumeRun Resume(Guid runId, Guid? bookmarkId) => new(runId, bookmarkId, tenant.TenantId!.Value, tenant.TenantIdentifier!);

    /// <summary>
    /// Stops a run, cancels its pending approvals and removes its open waits. Retries when the run was changed
    /// concurrently (the interpreter may be saving it); messages for the run that arrive later find it cancelled and do
    /// nothing.
    /// </summary>
    public async Task CancelAsync(WorkflowRun run, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            run.Status = RunStatus.Cancelled;
            run.WaitingOn = null;
            run.CompletedAt = time.GetUtcNow();
            foreach (var approval in await db.Approvals.Where(a => a.RunId == run.Id && a.Status == ApprovalStatus.Pending).ToListAsync(ct))
            {
                approval.Status = ApprovalStatus.Cancelled;
            }

            db.Bookmarks.RemoveRange(await db.Bookmarks.Where(b => b.RunId == run.Id && b.CompletedAt == null).ToListAsync(ct));
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

    /// <summary>
    /// Runs a failed run again from the node where it failed (an incident fixed, e.g. a list renamed back). The node's
    /// action keeps its execution id, so what an earlier attempt did is not repeated. False when the run cannot be retried.
    /// </summary>
    public async Task<bool> RetryAsync(WorkflowRun run, Guid? userId, CancellationToken ct)
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
        run.Attempts = 0;
        run.NodeAttempts = 0;
        run.LastActivityAt = now;
        var log = JsonNode.Parse(run.Log) as JsonArray ?? [];
        log.Add(new JsonObject { ["at"] = now.ToString("O"), ["message"] = $"Retried from {node}" + (userId is null ? string.Empty : " by a person") });
        run.Log = log.ToJsonString();
        await outbox.SaveChangesAsync(db, [], [Resume(run.Id, null)], ct);
        return true;
    }
}

/// <summary>
/// Every minute:
/// <list type="bullet">
/// <item>completes bookmarks whose time has come (delays, retries) and resumes their runs;</item>
/// <item>recovers runs that stopped making progress: running runs whose handler died (lease expired) or whose
/// message was lost or dead-lettered, and waiting runs whose bookmark was completed but not acted on;</item>
/// <item>escalates overdue approvals (adds <c>escalateTo</c> as assignees and notifies everyone).</item>
/// </list>
/// A run it recovers is not looked at again for <see cref="Recheck"/>, so slow or failing runs are not flooded with
/// messages; the interpreter's attempt limit ends runs that never make progress.
/// </summary>
internal sealed class WorkflowTimerJob(WorkflowsDbContext db, RunService runs, IOutbox outbox, ITenantContext tenant, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "workflows.timers";
    public const string Schedule = "* * * * *";
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(5);

    /// <summary>How long a running run may go without progress (and without a lease) before it is resumed.</summary>
    public static readonly TimeSpan Stalled = TimeSpan.FromMinutes(2);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var dueBookmarks = await db.Bookmarks.Where(b => b.CompletedAt == null && b.ResumeAt != null && b.ResumeAt <= now)
            .OrderBy(b => b.ResumeAt).Take(500).ToListAsync(cancellationToken);
        if (dueBookmarks.Count > 0)
        {
            foreach (var bookmark in dueBookmarks)
            {
                // A delay or retry is simply over; any other wait timed out (port timeout, else done).
                runs.Complete(bookmark, bookmark.Kind is BookmarkKinds.Delay or BookmarkKinds.Retry ? null : new JsonObject { ["outcome"] = "timeout" });
            }

            try
            {
                await outbox.SaveChangesAsync(db, [], [.. dueBookmarks.Select(b => runs.Resume(b.RunId, b.Id))], cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Completed (or removed) meanwhile: the rest is picked up next minute.
                db.ChangeTracker.Clear();
            }
        }

        var stalled = now - Stalled;
        var due = await db.Runs.AsNoTracking()
            .Where(r => r.NextCheckAt == null || r.NextCheckAt <= now)
            .Where(r => (r.Status == RunStatus.Running && r.LastActivityAt < stalled && (r.LeaseUntil == null || r.LeaseUntil < now))
                || (r.Status == RunStatus.Waiting && r.LastActivityAt < stalled
                    && db.Bookmarks.Any(b => b.Id == r.WaitingOn && b.CompletedAt != null && b.CompletedAt < stalled)))
            .OrderBy(r => r.LastActivityAt)
            .Select(r => new { r.Id, r.WaitingOn })
            .Take(500)
            .ToListAsync(cancellationToken);
        if (due.Count > 0)
        {
            await outbox.SaveChangesAsync(db, [], [.. due.Select(r => runs.Resume(r.Id, r.WaitingOn))], cancellationToken);
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

/// <summary>Options of the workflow module (<c>Workflows</c> section).</summary>
public sealed class WorkflowOptions
{
    /// <summary>Finished runs (and their approvals) are deleted after this many days.</summary>
    public int RunRetentionDays { get; set; } = 30;
}

/// <summary>Daily: deletes finished runs older than <see cref="WorkflowOptions.RunRetentionDays"/> with their approvals and bookmarks, old unclaimed completions, old AI call records and finished AI batches.</summary>
internal sealed class WorkflowRunCleanupJob(WorkflowsDbContext db, IOptions<WorkflowOptions> options, IOptions<WorkflowAiOptions> ai, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "workflows.runCleanup";
    public const string Schedule = "17 3 * * *";
    private const int Batch = 500;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-Math.Max(1, options.Value.RunRetentionDays));

        // Completions no run ever waited for.
        await db.Bookmarks.Where(b => b.RunId == WorkflowBookmarks.Unclaimed && b.CompletedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

        // Records of AI calls, kept as long as runs and the AI cache need them.
        var aiCutoff = time.GetUtcNow().AddDays(-Math.Max(Math.Max(1, options.Value.RunRetentionDays), ai.Value.CacheDays));
        await db.AiCalls.Where(c => c.CreatedAt < aiCutoff).ExecuteDeleteAsync(cancellationToken);
        await db.AiBatchRequests.Where(r => (r.Status == AiBatchRequestStatus.Completed || r.Status == AiBatchRequestStatus.Failed) && r.CompletedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        await db.AiBatches.Where(b => (b.Status == AiBatchPhase.Completed || b.Status == AiBatchPhase.Failed) && b.CompletedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
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
            await db.Bookmarks.Where(b => ids.Contains(b.RunId)).ExecuteDeleteAsync(cancellationToken);
            await db.Runs.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
