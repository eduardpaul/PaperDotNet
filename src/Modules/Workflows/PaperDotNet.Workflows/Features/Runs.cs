using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Taxonomy.Contracts;
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
    // Serial multi-page OCR can exceed Wolverine's 60-second default. Stay below the five-minute run lease.
    [MessageHandlerTimeout(240)]
    public static async Task Handle(ResumeRun message, ITenantScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateScope(message.TenantId, message.TenantIdentifier);
        await scope.ServiceProvider.GetRequiredService<WorkflowInterpreter>().RunAsync(message.RunId, message.BookmarkId, cancellationToken);
    }
}

/// <summary>A run to start; with an <see cref="Error"/> (e.g. a condition that cannot be checked) it is saved as failed.</summary>
internal sealed record WorkflowStart(
    WorkflowDefinition Workflow, WorkflowItem? Item, Guid? EventId, string? Data, int Depth, Guid? StartedBy, string? Error = null,
    JsonObject? Variables = null, string? Concurrency = null, string? TriggerType = null, WorkflowSpec? Spec = null, IReadOnlyList<WorkflowItem>? Items = null);

/// <summary>Starts runs of a workspace's workflows.</summary>
internal sealed class WorkflowStarter(
    WorkflowsDbContext db, IListItemStore items, IOutbox outbox, EventCausation causation, ITenantContext tenant, TimeProvider time,
    RunService runService, ITermStore terms, IUserDirectory users, BuiltInWorkflows builtIns)
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
        var concurrency = new Dictionary<Guid, string>();
        var starting = new HashSet<(Guid, Guid)>();
        foreach (var start in starts)
        {
            string? effectiveConcurrency = start.Concurrency;
            // One workflow on one item (ADR-0037): skip the new run, or cancel the one going.
            if (start.Item is { } target && start.Error is null)
            {
                if (!concurrency.TryGetValue(start.Workflow.Id, out var mode))
                {
                    var spec = start.Spec;
                    if (spec is null)
                    {
                        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == start.Workflow.Id && v.Number == start.Workflow.CurrentVersion, ct);
                        spec = DefinitionJson.Deserialize<WorkflowSpec>(version.Definition);
                    }
                    mode = spec.Concurrency ?? RunConcurrency.Parallel;
                    concurrency[start.Workflow.Id] = mode;
                }

                var members = start.Items ?? [target];
                var memberIds = members.Select(i => i.ItemId).ToArray();
                mode = effectiveConcurrency = start.Concurrency ?? mode;
                if (mode != RunConcurrency.Parallel)
                {
                    var going = await db.Runs
                        .Where(r => r.WorkflowId == start.Workflow.Id && (memberIds.Contains(r.ItemId!.Value) || db.RunItems.Any(i => i.RunId == r.Id && memberIds.Contains(i.ItemId))) && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting))
                        .ToListAsync(ct);
                    if (mode == RunConcurrency.Skip && (going.Count > 0 || members.Any(i => starting.Contains((start.Workflow.Id, i.ItemId)))))
                    {
                        continue;
                    }

                    foreach (var previous in going)
                    {
                        await runService.CancelAsync(previous, ct);
                    }
                }

                foreach (var member in members) starting.Add((start.Workflow.Id, member.ItemId));
            }

            var run = new WorkflowRun
            {
                Id = Ids.New(),
                Concurrency = effectiveConcurrency,
                WorkflowId = start.Workflow.Id,
                WorkflowVersion = start.Workflow.CurrentVersion,
                WorkspaceId = start.Workflow.WorkspaceId,
                ListId = start.Item?.ListId,
                ItemId = start.Item?.ItemId,
                EventId = start.EventId,
                IsSelection = start.Items is not null,
                Data = new JsonObject
                {
                    ["$workflowContext"] = new JsonObject
                    {
                        ["trigger"] = start.TriggerType ?? start.Workflow.Trigger.Split(',')[0],
                        ["input"] = start.Variables?.DeepClone() ?? new JsonObject(),
                        ["data"] = start.Data is null ? new JsonObject() : JsonNode.Parse(start.Data),
                        ["workspaceId"] = start.Workflow.WorkspaceId.ToString(),
                        ["listId"] = start.Item?.ListId.ToString(),
                        ["itemId"] = start.Item?.ItemId.ToString(),
                        ["items"] = new JsonArray([.. (start.Items ?? (start.Item is null ? [] : new[] { start.Item }))
                            .Select(i => (JsonNode)new JsonObject { ["workspaceId"] = i.WorkspaceId.ToString(), ["listId"] = i.ListId.ToString(), ["itemId"] = i.ItemId.ToString() })]),
                        ["userId"] = start.StartedBy?.ToString(),
                        ["startedAt"] = now.ToString("O"),
                    },
                }.ToJsonString(),
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
            var membership = start.Items ?? (start.Item is null ? [] : new[] { start.Item });
            for (var position = 0; position < membership.Count; position++)
            {
                var member = membership[position];
                db.RunItems.Add(new WorkflowRunItem { RunId = run.Id, ItemId = member.ItemId, WorkspaceId = member.WorkspaceId, ListId = member.ListId, Position = position });
            }
            runs.Add(run);
        }

        await outbox.SaveChangesAsync(db, [], messages, ct);
        return runs;
    }

    /// <summary>Most items one manual start covers.</summary>
    public const int MaxItemsPerStart = 100;

    /// <summary>
    /// Starts a workflow with the <c>manual</c> trigger: once per item, once for the selection, or once without an item when there are none
    /// (only for triggers without a list). Every item and the inputs are checked before anything starts; the inputs
    /// become the runs' variables.
    /// </summary>
    public Task<(List<WorkflowRun> Runs, string? Error)> StartManualAsync(
        WorkflowDefinition workflow, IReadOnlyList<WorkflowItem> targets, JsonObject? inputs, Guid? startedBy, CancellationToken ct, Guid? primaryItemId = null) =>
        StartOnDemandAsync(workflow, targets, inputs, startedBy, WorkflowTriggers.Manual, ct, primaryItemId);

    /// <summary>Validates and starts an opted-in manual or webhook workflow.</summary>
    public async Task<(List<WorkflowRun> Runs, string? Error)> StartOnDemandAsync(
        WorkflowDefinition workflow, IReadOnlyList<WorkflowItem> targets, JsonObject? inputs, Guid? startedBy, string triggerType, CancellationToken ct, Guid? primaryItemId = null)
    {
        var name = workflow.Name;
        if (!TriggerColumn.Contains(workflow.Trigger, triggerType))
        {
            var reason = triggerType == WorkflowTriggers.Manual ? "is not started manually" : $"does not support {triggerType}";
            return ([], $"The workflow '{name}' {reason} (its triggers: {workflow.Trigger.Replace(",", ", ", StringComparison.Ordinal)}).");
        }

        BuiltInWorkflow? builtIn = null;
        if (workflow.BuiltInKey is { } key)
        {
            builtIn = await builtIns.FindAsync(key, ct);
            if (builtIn is null || !builtIns.IsAvailable(builtIn))
            {
                return ([], $"The workflow '{name}' is unavailable.");
            }
        }

        if (!workflow.Enabled && !(triggerType == WorkflowTriggers.Manual && builtIn?.AllowManualLaunch == true))
        {
            return ([], $"The workflow '{name}' is disabled.");
        }

        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == workflow.Id && v.Number == workflow.CurrentVersion, ct);
        var spec = DefinitionJson.Deserialize<WorkflowSpec>(version.Definition);
        var trigger = spec.AllTriggers.First(t => t.Type == triggerType);
        var selection = triggerType == WorkflowTriggers.Manual && trigger.SelectionMode == "selection";
        if (primaryItemId is { } requestedPrimary && !targets.Any(t => t.ItemId == requestedPrimary)) return ([], "primaryItemId must belong to the selection.");
        if (selection && (targets.Count is 0 or > MaxItemsPerStart || targets.Select(t => t.ListId).Distinct().Count() != 1))
            return ([], "Choose 1–100 items from one list for a selection workflow.");
        var schema = spec.InputSchema ?? trigger.Inputs;
        inputs = WorkflowInputs.WithDefaults(schema, inputs);
        if ((WorkflowInputs.Check(schema, inputs) ?? await DomainInputs.CheckAsync(schema, inputs, items, terms, users, ct)) is { } invalid)
        {
            return ([], invalid);
        }

        if (spec.Scope == "workspace" && targets.Count > 0)
        {
            return ([], "A workspace workflow runs without items.");
        }

        if (workflow.ListId is { } scopedList && targets.Any(t => t.ListId != scopedList))
        {
            return ([], "The workflow belongs to a different list.");
        }

        if (targets.Count == 0 && (spec.Scope == "list" || workflow.ListId is not null) && trigger.List is null)
        {
            return ([], "Choose items for this list workflow.");
        }

        if (targets.Count == 0 && (trigger.ContentType is not null || trigger.Terms is { Count: > 0 }))
        {
            return ([], "Choose items for a workflow with item filters.");
        }

        if (triggerType == WorkflowTriggers.Webhook && (targets.Count > 0 || trigger.List is not null || workflow.ListId is not null || spec.Scope == "list"))
        {
            return ([], "Webhook triggers run in the workspace without items.");
        }

        if (targets.Count == 0 && trigger.List is { } needed)
        {
            return ([], $"The workflow '{name}' runs on items of the list '{needed}'; choose items.");
        }

        var store = items.AsSystem();
        var lists = new Dictionary<Guid, ListData?>();
        foreach (var item in targets)
        {
            if (!lists.TryGetValue(item.ListId, out var list))
            {
                list = await store.GetListAsync(item.WorkspaceId, item.ListId, ct);
                lists[item.ListId] = list;
            }
            if (trigger.List is { } listName && list?.Name != listName)
            {
                return ([], $"The workflow '{name}' only runs on items of the list '{listName}'.");
            }

            if (trigger.ContentType is { } type)
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

        if (selection)
        {
            var primary = targets.First(t => t.ItemId == (primaryItemId ?? targets[0].ItemId));
            var grouped = await StartAsync([new WorkflowStart(workflow, primary, null, null, causation.Depth, startedBy,
                Variables: inputs, TriggerType: triggerType, Spec: spec, Concurrency: trigger.Concurrency, Items: targets)], ct);
            return (grouped, null);
        }

        var each = targets.Count == 0 ? [null] : targets.Select(t => (WorkflowItem?)t).ToList();
        var runs = await StartAsync([.. each.Select(item => new WorkflowStart(workflow, item, null, null, causation.Depth, startedBy, Variables: inputs, TriggerType: triggerType, Spec: spec, Concurrency: trigger.Concurrency))], ct);
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
            if ((property as JsonObject)?["type"] is not JsonValue type || !Types.Contains(type.ToString()))
            {
                yield return $"inputs.{name} needs a type ({string.Join(", ", Types)}).";
            }
        }

        if (schema["type"] is { } rootType && rootType.ToString() != "object")
        {
            yield return "inputs must describe an object.";
        }

        foreach (var error in ValidateConstraints(schema))
        {
            yield return error;
        }

        foreach (var required in schema["required"] as JsonArray ?? [])
        {
            if (required?.ToString() is not { } name || !properties.ContainsKey(name))
            {
                yield return $"inputs.required names '{required}', which is not a property.";
            }
        }
    }

    private static IEnumerable<string> ValidateConstraints(JsonObject schema)
    {
        foreach (var error in DomainInputs.Validate(schema))
        {
            yield return error;
        }

        foreach (var (name, value) in schema)
        {
            if (name is "minLength" or "maxLength" or "minItems" or "maxItems" && (value is not JsonValue size || !size.TryGetValue<int>(out var length) || length < 0))
            {
                yield return $"inputs.{name} must be a nonnegative integer.";
            }

            if (name is "minimum" or "maximum" && (value is not JsonValue bound || bound.GetValueKind() != System.Text.Json.JsonValueKind.Number))
            {
                yield return $"inputs.{name} must be a number.";
            }

            if (name == "uniqueItems" && value?.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            {
                yield return "inputs.uniqueItems must be a boolean.";
            }

            if (name == "enum" && value is not JsonArray { Count: > 0 })
            {
                yield return "inputs.enum must be a nonempty array.";
            }

            if (name == "required" && value is not JsonArray)
            {
                yield return "inputs.required must be an array.";
            }
        }

        foreach (var (_, property) in schema["properties"] as JsonObject ?? [])
        {
            if (property is JsonObject child)
            {
                foreach (var error in child["type"]?.ToString() == "object" ? ValidateSchema(child) : ValidateConstraints(child))
                {
                    yield return error;
                }
            }
        }

        if (schema["items"] is JsonObject item)
        {
            foreach (var error in item["type"]?.ToString() == "object" ? ValidateSchema(item) : ValidateConstraints(item))
            {
                yield return error;
            }
        }
    }

    /// <summary>Why the values do not fit the schema, or null (no schema: any values).</summary>
    public static string? Check(JsonObject? schema, JsonObject? values) => CheckObject(schema, values ?? [], "inputs");

    private static string? CheckObject(JsonObject? schema, JsonObject values, string path)
    {
        if (schema?["properties"] is not JsonObject properties)
        {
            return null;
        }

        foreach (var required in schema["required"] as JsonArray ?? [])
        {
            if (required?.ToString() is { } name && (values[name] is null || properties[name]?["x-paperdotnet"] is not null && (values[name] is JsonArray { Count: 0 } || values[name]?.ToString() == string.Empty)))
            {
                return $"The input '{name}' is required.";
            }
        }

        foreach (var (name, value) in values)
        {
            if (properties[name] is not JsonObject definition || definition["type"] is not JsonValue)
            {
                return $"Unknown input '{name}'.";
            }

            if (CheckValue(definition, value, name, $"{path}.{name}") is { } error)
            {
                return error;
            }
        }

        return CheckConstraints(schema, values, path);
    }

    private static string? CheckValue(JsonObject schema, JsonNode? value, string name, string path)
    {
        var kind = value?.GetValueKind();
        var type = schema["type"]?.ToString();
        var fits = type switch
        {
            "string" => kind == System.Text.Json.JsonValueKind.String,
            "number" => kind == System.Text.Json.JsonValueKind.Number,
            "integer" => kind == System.Text.Json.JsonValueKind.Number && Number(value!) == Math.Truncate(Number(value!)),
            "boolean" => kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
            "array" => kind == System.Text.Json.JsonValueKind.Array,
            "object" => kind == System.Text.Json.JsonValueKind.Object,
            _ => false,
        };
        if (!fits)
        {
            return $"The input '{name}' must be of type {type}.";
        }

        if (CheckConstraints(schema, value, path) is { } error)
        {
            return error;
        }

        if (value is JsonObject obj)
        {
            return CheckObject(schema, obj, path);
        }

        if (value is JsonArray array && schema["items"] is JsonObject itemSchema)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (CheckValue(itemSchema, array[index], $"{name}[{index}]", $"{path}[{index}]") is { } invalid)
                {
                    return invalid;
                }
            }
        }

        return null;
    }

    public static JsonObject WithDefaults(JsonObject? schema, JsonObject? values)
    {
        var result = values?.DeepClone().AsObject() ?? [];
        foreach (var (name, property) in schema?["properties"] as JsonObject ?? [])
        {
            if (!result.ContainsKey(name) && property is JsonObject definition && definition.TryGetPropertyValue("default", out var value))
            {
                result[name] = value?.DeepClone();
            }

            if (property is JsonObject childSchema && result[name] is JsonObject child)
            {
                result[name] = WithDefaults(childSchema, child);
            }
        }

        return result;
    }

    private static string? CheckConstraints(JsonObject? schema, JsonNode? value, string path)
    {
        if (schema is null)
        {
            return null;
        }

        if (value is JsonArray array)
        {
            if (schema["minItems"] is JsonValue min && array.Count < min.GetValue<int>()
                || schema["maxItems"] is JsonValue max && array.Count > max.GetValue<int>())
            {
                return $"{path} has an invalid number of selections.";
            }

            if (schema["uniqueItems"]?.ToString() == "true" && array.Where((item, index) => array.Take(index).Any(other => JsonNode.DeepEquals(item, other))).Any())
            {
                return $"{path} requires unique selections.";
            }
        }

        if (schema["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(choice, value)))
        {
            return $"{path} must be one of the declared choices.";
        }

        if (value is JsonValue scalar && scalar.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            var length = scalar.GetValue<string>().Length;
            if (schema["minLength"] is JsonValue min && length < min.GetValue<int>()
                || schema["maxLength"] is JsonValue max && length > max.GetValue<int>())
            {
                return $"{path} has an invalid length.";
            }
        }

        if (value is JsonValue number && number.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            var amount = Number(number);
            if (schema["minimum"] is JsonValue min && amount < Number(min)
                || schema["maximum"] is JsonValue max && amount > Number(max))
            {
                return $"{path} is outside the allowed range.";
            }
        }

        return null;
    }

    private static double Number(JsonNode value) => double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
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

        var link = approval.ListId is { } listId ? new NotificationLink(approval.WorkspaceId, listId, approval.ItemId) : null;
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
    ScriptRunner scripts,
    RunService runService,
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
    /// <summary>Nodes one execution runs before it saves and continues in a new message (so it does not hold a server long).</summary>
    private const int MaxNodesPerExecution = 500;
    private const int MaxNodesPerRun = 10_000;

    /// <summary>How often a script step's applied writes are saved (they are safe to repeat, so this only limits repeats).</summary>
    private const int ScriptSaveEvery = 25;
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
        var storedData = run.Data is { } json ? JsonNode.Parse(json) as JsonObject : null;
        var executionContext = storedData?["$workflowContext"] as JsonObject;
        var data = executionContext is null ? storedData : executionContext["data"] as JsonObject;
        void Log(string message)
        {
            log.Add(new JsonObject { ["at"] = time.GetUtcNow().ToString("O"), ["message"] = message });
            while (log.Count > MaxLogEntries)
            {
                log.RemoveAt(0);
            }
        }

        // Saves progress and extends the lease; releases it when the run stops (finished or waiting) or pauses.
        Task SaveAsync(IReadOnlyCollection<ITenantMessage>? messages = null, bool pause = false, IReadOnlyCollection<IntegrationEvent>? events = null)
        {
            var now = time.GetUtcNow();
            run.LastActivityAt = now;
            run.Log = log.ToJsonString();
            run.Outputs = outputs.ToJsonString();
            run.Variables = variables.ToJsonString();
            var stopped = run.Status != RunStatus.Running || pause;
            run.LeaseId = stopped ? null : lease;
            run.LeaseUntil = stopped ? null : now + Lease;
            return messages is { Count: > 0 } || events is { Count: > 0 } ? outbox.SaveChangesAsync(db, events ?? [], messages, ct) : db.SaveChangesAsync(ct);
        }

        // An event of this workflow (ADR-0038): wf.{key}.{name}, on the run's item, one deeper than the run. Its id comes
        // from the run and what raised it, so saving it again (a retried message) raises nothing twice.
        WorkflowTriggerRaised Event(string name, JsonObject? payload, params object[] source) => new()
        {
            EventId = TriggerSchedules.EventId(["wf", run.Id, name, .. source]),
            TenantId = tenant.TenantId!.Value,
            TenantIdentifier = tenant.TenantIdentifier!,
            UserId = run.StartedBy,
            Depth = run.Depth + 1,
            Trigger = $"{WorkflowTriggers.WorkflowEventPrefix}{workflow.EventKey}.{name}",
            WorkspaceId = run.WorkspaceId,
            ListId = run.ListId,
            ItemId = run.ItemId,
            Data = payload?.ToJsonString(),
        };

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
            await SaveAsync(events: [Event(WorkflowEvents.Failed,
                new JsonObject { ["runId"] = run.Id.ToString(), ["status"] = "failed", ["error"] = run.Error }, run.CompletedAt.Value.UtcTicks)]);
            LogRunFailed(run.Id, error);
        }

        async Task CompleteAsync()
        {
            run.Status = RunStatus.Completed;
            run.CompletedAt = time.GetUtcNow();
            Log("Completed");
            await SaveAsync(events: [Event(WorkflowEvents.Completed, new JsonObject { ["runId"] = run.Id.ToString(), ["status"] = "completed" })]);
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
                var bookmark = NewBookmark(run, id, BookmarkKinds.Retry, TimerKey(run), time.GetUtcNow().AddMinutes(delay));
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
            scope ??= await TokenScope.LoadAsync(items, item, outputs, variables, data, ct, executionContext);
            return await tokens.ExpandAsync(template, scope, ct);
        }

        // An order of loop events in this run (one more than any loop recorded): tells which loops started in a pass.
        long LoopStamp() => 1 + flow.Nodes.Where(n => n.Value.Activity == FlowActivities.ForEach)
            .Select(n => outputs[n.Key] as JsonObject)
            .SelectMany(o => new[] { o?["started"], o?["pass"] })
            .Select(v => v is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0)
            .DefaultIfEmpty(0).Max();

        // The elements a forEach goes through: its items (an array or one token), or the items its query finds.
        async Task<(JsonArray? Elements, string? Error)> ElementsAsync(JsonObject inputs)
        {
            JsonNode? value;
            if (inputs["query"] is JsonObject query)
            {
                var store = items.AsSystem();
                var listName = await ExpandAsync(ActivityInputs.Text(query, "list")!);
                if ((await store.GetListsAsync(run.WorkspaceId, null, ct)).FirstOrDefault(l => l.Name == listName) is not { } list)
                {
                    return (null, $"The list '{listName}' does not exist in the workspace.");
                }

                var filter = ActivityInputs.Text(query, "filter") is { } text ? await ExpandAsync(text) : null;
                var (found, problem) = await store.QueryAsync(run.WorkspaceId, list.Id, new ListItemQuery(filter, Top: FlowActivities.MaxForEachItems + 1), ct);
                if (problem is not null)
                {
                    return (null, problem);
                }

                value = new JsonArray([.. found.Select(i =>
                {
                    var element = i.Fields.DeepClone().AsObject();
                    element["id"] = i.Id.ToString();
                    return (JsonNode)element;
                })]);
            }
            else if (inputs["items"] is JsonValue token && token.TryGetValue<string>(out var template))
            {
                scope ??= await TokenScope.LoadAsync(items, item, outputs, variables, data, ct, executionContext);
                value = await tokens.ValueAsync(template, scope, ct);
            }
            else
            {
                value = inputs["items"]?.DeepClone();
            }

            return value switch
            {
                null => ([], null),
                JsonArray { Count: > FlowActivities.MaxForEachItems } => (null, $"forEach goes through at most {FlowActivities.MaxForEachItems} elements."),
                JsonArray array => (array, null),
                _ => (null, "items is not a list."),
            };
        }

        // A script step: runs the script once and saves its planned writes, then applies them (again after a crash or a
        // retry, without running the script again). False when the run stopped.
        async Task<bool> ScriptAsync(string id, FlowNode node)
        {
            if (run.StepExecutionId is null)
            {
                run.StepExecutionId = Ids.New();
                await SaveAsync();
            }

            if (outputs[id] is not JsonObject { } state || state["plan"] is not JsonArray)
            {
                scope ??= await TokenScope.LoadAsync(items, item, outputs, variables, data, ct, executionContext);
                var outcome = scripts.Run(ScriptRunner.Code(node.Inputs ?? [])!, run.WorkspaceId, run.StepExecutionId.Value, scope.Item, scope.ListName,
                    outputs, variables, data, ct, executionContext);
                foreach (var line in outcome.Log.Take(20))
                {
                    Log($"{id}: {Truncate(line)}");
                }

                if (outcome.Error is { } failure)
                {
                    return await FailedAsync(id, node, failure);
                }

                variables = outcome.Variables;
                state = new JsonObject { ["plan"] = outcome.Plan, ["applied"] = 0, ["result"] = outcome.Result };
                outputs[id] = state;
                if (outputs.ToJsonString().Length > MaxStateLength || variables.ToJsonString().Length > MaxStateLength)
                {
                    await FailAsync($"{id}: the script's result, variables or planned writes are too large.", id);
                    return false;
                }

                await SaveAsync();
            }

            var plan = (JsonArray)state["plan"]!;
            var store = items.AsSystem();
            var created = new JsonArray();
            int updated = 0, deleted = 0;
            for (var index = 0; index < plan.Count; index++)
            {
                var write = (JsonObject)plan[index]!;
                var op = write["op"]!.GetValue<string>();
                var listId = Guid.Parse(write["listId"]!.GetValue<string>());
                var target = Guid.Parse(write["id"]!.GetValue<string>());
                if (index < state["applied"]!.GetValue<int>())
                {
                    Count();
                    continue;
                }

                var result = op switch
                {
                    "create" => await store.CreateAsync(run.WorkspaceId, listId, target, (JsonObject)write["fields"]!.DeepClone(), null, ct),
                    "update" => await store.UpdateAsync(run.WorkspaceId, listId, target, (JsonObject)write["fields"]!.DeepClone(), null, ct),
                    "relate" => await store.AddRelationshipAsync(target, Guid.Parse(write["fields"]!["otherId"]!.GetValue<string>()), new RelationshipOptions(write["fields"]!["type"]?.GetValue<string>(), Attributes: write["fields"]!["attributes"] as JsonObject), ct),
                    "updateRelationship" => await UpdateEdgeAsync(),
                    "unrelate" => await store.RemoveRelationshipAsync(target, Guid.Parse(write["fields"]!["otherId"]!.GetValue<string>()), ct),
                    "deleteGlobal" => await DeleteGlobalAsync(target),
                    _ => await store.DeleteAsync(run.WorkspaceId, listId, target, null, ct),
                };
                if (!result.Succeeded && !(op is "delete" or "deleteGlobal" or "unrelate" && result.Status == ListItemStatus.NotFound))
                {
                    return await FailedAsync(id, node, $"write {index + 1} ({op} in {write["list"]}): {result.Describe()}");
                }

                Count();
                state["applied"] = index + 1;
                if ((index + 1) % ScriptSaveEvery == 0)
                {
                    await SaveAsync();
                }

                async Task<ListItemResult> UpdateEdgeAsync()
                {
                    var edgeId = Guid.Parse(write["fields"]!["otherId"]!.GetValue<string>());
                    var attributes = write["fields"]!["attributes"]!.AsObject();
                    var version = write["fields"]!["version"]!.GetValue<uint>();
                    var result = await store.UpdateRelationshipAsync(target, edgeId, attributes, version, ct);
                    if (result.Status != ListItemStatus.VersionMismatch) return result;
                    var edge = await store.GetRelationshipAsync(target, edgeId, ct);
                    // A resumed plan may repeat the write after it saved but before its checkpoint saved.
                    if (edge?.Version == version + 1 && attributes.All(p => p.Value is null ? !edge.Attributes.ContainsKey(p.Key)
                        : edge.Attributes.TryGetPropertyValue(p.Key, out var current) && JsonNode.DeepEquals(current, p.Value))) return new(ListItemStatus.Ok);
                    return result;
                }

                async Task<ListItemResult> DeleteGlobalAsync(Guid itemId)
                {
                    var current = await store.GetByIdAsync(itemId, ct);
                    return current is null ? new ListItemResult(ListItemStatus.NotFound) : await store.DeleteAsync(current.WorkspaceId, current.ListId, itemId, null, ct);
                }

                void Count()
                {
                    switch (op)
                    {
                        case "create":
                            created.Add(target.ToString());
                            break;
                        case "update":
                            updated++;
                            break;
                        case "updateRelationship":
                        case "relate":
                        case "unrelate":
                            break;
                        default:
                            deleted++;
                            break;
                    }
                }
            }

            outputs[id] = new JsonObject { ["result"] = state["result"]?.DeepClone(), ["created"] = created, ["updated"] = updated, ["deleted"] = deleted };
            Log($"{id}: script done ({plan.Count} write(s))");
            return await ContinueAsync(id, "done");
        }

        if (run.Attempts > MaxAttempts)
        {
            await FailAsync($"The run stopped after {MaxAttempts} attempts without progress (see the server log).", run.Node);
            return;
        }

        if (run.Node is null)
        {
            // Runs of this workflow on the item that started at the same moment did not see each other: the earlier one
            // wins (skip) or the later one does (replace).
            if ((run.Concurrency ?? spec.Concurrency) is RunConcurrency.Skip or RunConcurrency.Replace && run.ItemId is { } runItem)
            {
                var memberIds = await db.RunItems.Where(i => i.RunId == run.Id).Select(i => i.ItemId).ToListAsync(ct);
                if (memberIds.Count == 0) memberIds.Add(runItem);
                var others = await db.Runs
                    .Where(r => r.WorkflowId == run.WorkflowId && (memberIds.Contains(r.ItemId!.Value) || db.RunItems.Any(i => i.RunId == r.Id && memberIds.Contains(i.ItemId))) && r.Id != run.Id && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting))
                    .ToListAsync(ct);
                var earlier = others.Where(r => r.StartedAt < run.StartedAt || (r.StartedAt == run.StartedAt && r.Id.CompareTo(run.Id) < 0)).ToList();
                if ((run.Concurrency ?? spec.Concurrency) == RunConcurrency.Skip && earlier.Count > 0)
                {
                    Log("Skipped: another run of this workflow on the item is going (concurrency: skip).");
                    run.Status = RunStatus.Cancelled;
                    run.CompletedAt = time.GetUtcNow();
                    await SaveAsync();
                    return;
                }

                foreach (var older in (run.Concurrency ?? spec.Concurrency) == RunConcurrency.Replace ? earlier : [])
                {
                    await runService.CancelAsync(older, ct);
                }
            }

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

            // The wait ended: that is progress (a node that polls with run-again waits never advances in between).
            run.Status = RunStatus.Running;
            run.WaitingOn = null;
            run.NextCheckAt = null;
            run.Attempts = 0;
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
            if (run.Executed >= MaxNodesPerRun)
            {
                await FailAsync($"The workflow ran more than {MaxNodesPerRun} steps.", run.Node);
                return;
            }

            if (executed >= MaxNodesPerExecution)
            {
                // Long runs (loops) continue in a new message instead of holding this server.
                await SaveAsync([new ResumeRun(run.Id, null, tenant.TenantId!.Value, tenant.TenantIdentifier!)], pause: true);
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
                    await FailAsync(await ExpandAsync(ActivityInputs.Text(inputs, "message") ?? "The workflow ended with a failure."), null);
                    return;
                case FlowActivities.Raise:
                    // Raises wf.{key}.{event} with data (strings may be tokens; one token keeps its type), saved with the run.
                    run.StepExecutionId ??= Ids.New();
                    var eventName = ActivityInputs.Text(inputs, "event")!;
                    var payload = new JsonObject();
                    foreach (var (field, value) in inputs["data"] as JsonObject ?? [])
                    {
                        scope ??= await TokenScope.LoadAsync(items, item, outputs, variables, data, ct, executionContext);
                        payload[field] = value is JsonValue raw && raw.TryGetValue<string>(out var valueTemplate)
                            ? await tokens.ValueAsync(valueTemplate, scope, ct)
                            : value?.DeepClone();
                    }

                    // Saved before moving on: a retry raises it again with the same id, which starts nothing twice.
                    Log($"{id}: raised wf.{workflow.EventKey}.{eventName}");
                    outputs[id] = new JsonObject { ["event"] = $"{WorkflowTriggers.WorkflowEventPrefix}{workflow.EventKey}.{eventName}" };
                    await SaveAsync(events: [Event(eventName, payload, id, run.StepExecutionId.Value)]);
                    if (!await ContinueAsync(id, "done"))
                    {
                        return;
                    }

                    break;
                case FlowActivities.SetVariable:
                    var name = ActivityInputs.Text(inputs, "name")!;
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
                case FlowActivities.ForEach:
                    // The loop's state is the node's output: the elements, the current index and the count. The body
                    // leads back here, which moves on to the next element; after the last, the node continues with done.
                    var loop = outputs[id] as JsonObject;
                    var active = loop?["active"] is JsonValue flag && flag.GetValueKind() == System.Text.Json.JsonValueKind.True && loop["items"] is JsonArray;
                    var (elements, elementsError) = active ? ((JsonArray)loop!["items"]!, null) : await ElementsAsync(inputs);
                    if (elements is null)
                    {
                        if (!await FailedAsync(id, node, elementsError!))
                        {
                            return;
                        }

                        break;
                    }

                    var position = active ? loop!["index"]!.GetValue<int>() + 1 : 0;
                    var element = ActivityInputs.Text(inputs, "as") ?? "item";
                    if (position < elements.Count)
                    {
                        if (active)
                        {
                            // Loops that started during the previous element's pass are inside this loop: they start again
                            // for this element, also when that pass left them early. (By time, not by shape: an early exit
                            // makes loops reach each other both ways.)
                            var pass = loop!["pass"]?.GetValue<long>() ?? 0;
                            foreach (var inner in Definitions.LoopBody(flow, id).Where(n => flow.Nodes[n].Activity == FlowActivities.ForEach))
                            {
                                if (outputs[inner] is JsonObject innerLoop && (innerLoop["started"]?.GetValue<long>() ?? 0) >= pass)
                                {
                                    innerLoop["active"] = false;
                                }
                            }

                            loop["index"] = position;
                            loop["pass"] = LoopStamp();
                        }
                        else
                        {
                            Log($"{id}: {elements.Count} element(s)");
                            var stamp = LoopStamp();
                            outputs[id] = new JsonObject
                            {
                                ["active"] = true,
                                ["index"] = position,
                                ["count"] = elements.Count,
                                ["started"] = stamp,
                                ["pass"] = stamp,
                                ["items"] = elements,
                            };
                        }

                        variables[element] = elements[position]?.DeepClone();

                        if (outputs.ToJsonString().Length > MaxStateLength)
                        {
                            await FailAsync($"{id}: the elements are too large.", id);
                            return;
                        }

                        if (!await ContinueAsync(id, "item"))
                        {
                            return;
                        }
                    }
                    else
                    {
                        variables.Remove(element);
                        outputs[id] = new JsonObject { ["index"] = elements.Count, ["count"] = elements.Count };
                        if (!await ContinueAsync(id, "done"))
                        {
                            return;
                        }
                    }

                    break;
                case FlowActivities.Script:
                    if (!await ScriptAsync(id, node))
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
                    var hours = ActivityInputs.Number(inputs, "hours") ?? 0;
                    Log($"{id}: waiting {hours} hours");
                    await WaitAsync(NewBookmark(run, id, BookmarkKinds.Delay, TimerKey(run), time.GetUtcNow().AddHours(hours)));
                    return;
                case FlowActivities.Approval:
                    if (item is null && inputs["review"] is not null)
                    {
                        await FailAsync($"{id}: a file review needs an item (the trigger has none).", id);
                        return;
                    }

                    var approvalInputs = inputs.DeepClone().AsObject();
                    foreach (var people in new[] { "assignees", "escalateTo" })
                    {
                        if (approvalInputs[people] is JsonValue peopleTemplate && peopleTemplate.TryGetValue<string>(out var peopleText))
                        {
                            scope ??= await TokenScope.LoadAsync(items, item, outputs, variables, data, ct, executionContext);
                            approvalInputs[people] = await tokens.ValueAsync(peopleText, scope, ct);
                        }
                    }

                    var approval = await CreateApprovalAsync(run, item, id, approvalInputs, ExpandAsync, ct);
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

                    // A run-again wait of this node that ended is handed back to the activity (its data and payload) until
                    // the node moves on, so a retry after a failure or crash sees it too.
                    var resumed = await db.Bookmarks
                        .Where(b => b.RunId == run.Id && b.Node == id && b.RunAgain && b.CompletedAt != null)
                        .OrderByDescending(b => b.CompletedAt)
                        .FirstOrDefaultAsync(ct);
                    void Consumed()
                    {
                        if (resumed is not null)
                        {
                            db.Bookmarks.Remove(resumed);
                        }
                    }

                    var result = await executor.ExecuteAsync(
                        new ActionDefinition(node.Activity, node.Inputs), run.WorkspaceId, item, run.StartedBy, data, outputs, variables,
                        $"workflow:{workflow.Name}", $"run:{run.Id:N}:{run.StepExecutionId.Value:N}", run.StepExecutionId.Value, ct, run.Id,
                        resumed is null ? null : new WorkflowResumedWait(resumed.Kind, resumed.Key, JsonObjectOf(resumed.Data), JsonObjectOf(resumed.Payload)), executionContext);
                    if (!result.Succeeded)
                    {
                        if (!await FailedAsync(id, node, result.Error ?? "The action failed."))
                        {
                            return;
                        }

                        Consumed(); // continued on the error port
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
                        if (existing is { CompletedAt: not null } && existing.RunId == run.Id && (wait.RunAgain || existing == resumed))
                        {
                            // The node ran again and waits for the same thing again (e.g. the next poll): wait anew.
                            existing.CompletedAt = null;
                            existing.Payload = null;
                            existing.ResumeAt = wait.ResumeAt;
                        }

                        if (existing != resumed)
                        {
                            Consumed();
                        }

                        waitOn.RunId = run.Id;
                        waitOn.Node = id;
                        waitOn.RunAgain = wait.RunAgain;
                        waitOn.Data = wait.Data?.ToJsonString();
                        if (waitOn.Data?.Length > MaxStateLength)
                        {
                            await FailAsync($"{id}: the data of the wait is too large.", id);
                            return;
                        }

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
                    Consumed();
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

    /// <summary>The key of a delay or retry wait: new each time (a retried run counts its steps from 0 again), and short.</summary>
    private static string TimerKey(WorkflowRun run) => $"{run.Id:N}:{Ids.New():N}";

    private static JsonObject? JsonObjectOf(string? json) => json is null ? null : JsonNode.Parse(json) as JsonObject;

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
        if (ActivityInputs.Text(inputs, "step") is { } step)
        {
            return (outputs[step]?["outcome"] is JsonValue outcome && outcome.ToString() == ActivityInputs.Text(inputs, "is"), null);
        }

        if (ActivityInputs.Text(inputs, "op") is { } op)
        {
            var left = ActivityInputs.Text(inputs, "left") is { } l ? await expand(l) : string.Empty;
            var right = ActivityInputs.Text(inputs, "right") is { } r ? await expand(r) : string.Empty;
            return (Comparison.Holds(left, op, right), null);
        }

        if (item is null)
        {
            return (false, "a filter needs an item (the trigger has none).");
        }

        var (matches, error) = await items.AsSystem().QueryAsync(item.WorkspaceId, item.ListId, new ListItemQuery($"id eq {item.ItemId} and ({ActivityInputs.Text(inputs, "filter")})", Top: 1), ct);
        return (matches.Count > 0, error);
    }

    /// <summary>The pending request of this node (reused when the node runs again), or a new one (not saved yet).</summary>
    private async Task<ApprovalRequest?> CreateApprovalAsync(
        WorkflowRun run, WorkflowItem? item, string node, JsonObject inputs, Func<string, Task<string>> expand, CancellationToken ct)
    {
        var existing = await db.Approvals.FirstOrDefaultAsync(a => a.RunId == run.Id && a.StepName == node && a.Status == ApprovalStatus.Pending, ct);
        if (existing is not null)
        {
            return existing;
        }

        var current = item is null ? null : await items.AsSystem().GetAsync(item.WorkspaceId, item.ListId, item.ItemId, ct);
        var (assignees, _) = await recipients.ResolveAsync(ActivityInputs.Texts(inputs, "assignees") ?? [], current, run.StartedBy, ct);
        if (assignees.Count == 0)
        {
            return null;
        }

        var (escalateTo, _) = await recipients.ResolveAsync(ActivityInputs.Texts(inputs, "escalateTo") ?? [], current, run.StartedBy, ct);
        var title = await expand(ActivityInputs.Text(inputs, "title") ?? $"Approve {{title}} ({node})");
        var approval = new ApprovalRequest
        {
            Id = Ids.New(),
            RunId = run.Id,
            StepName = node,
            WorkspaceId = run.WorkspaceId,
            ListId = item?.ListId,
            ItemId = item?.ItemId,
            InputSchema = (inputs["inputSchema"] as JsonObject)?.ToJsonString(),
            Title = title.Length > 1000 ? title[..1000] : title,
            ReviewType = inputs["review"] is JsonObject review ? ActivityInputs.Text(review, "type") : null,
            ReviewKey = inputs["review"] is JsonObject reference && ActivityInputs.Text(reference, "key") is { } reviewKey ? await expand(reviewKey) : null,
            Assignees = assignees,
            EscalateTo = [.. escalateTo.Except(assignees)],
            DueAt = ActivityInputs.Number(inputs, "dueInHours") is { } hours ? time.GetUtcNow().AddHours(hours) : null,
            Status = ApprovalStatus.Pending,
        };
        db.Approvals.Add(approval);
        return approval;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow run {RunId} failed: {Error}")]
    private partial void LogRunFailed(Guid runId, string error);
}

/// <summary>Decisions on approvals, completing bookmarks, and cancelling and retrying runs.</summary>
internal sealed class RunService(
    WorkflowsDbContext db, IOutbox outbox, ITenantContext tenant, TimeProvider time, IItemActivity activity,
    IListItemStore items, ITermStore terms, IUserDirectory users, IEnumerable<IApprovalReviewProvider> reviews)
{
    public enum DecisionResult
    {
        Ok,
        NotFound,
        AlreadyDecided,
        InvalidReview,
        InvalidInputs,
    }

    /// <summary>
    /// Records the decision of an assignee and completes the approval's bookmark; the message that resumes the run is
    /// stored with it. A concurrent change of the request (e.g. an escalation adding assignees) is retried.
    /// </summary>
    public async Task<(DecisionResult Result, string? Error)> DecideAsync(Guid approvalId, Guid userId, string outcome, string? comment, CancellationToken ct, JsonObject? inputs = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == approvalId, ct);
            if (approval is null || !approval.Assignees.Contains(userId))
            {
                return (DecisionResult.NotFound, null);
            }

            if (approval.Status != ApprovalStatus.Pending)
            {
                return (DecisionResult.AlreadyDecided, null);
            }

            if (approval.ReviewType is { } type)
            {
                var provider = reviews.SingleOrDefault(p => p.Type == type);
                if (approval.ListId is null || approval.ItemId is null)
                {
                    return (DecisionResult.InvalidReview, null);
                }

                var context = new ApprovalReviewContext(approval.Id, approval.RunId,
                    new WorkflowItem(approval.WorkspaceId, approval.ListId!.Value, approval.ItemId!.Value), approval.ReviewKey ?? "");
                if (await items.GetAsync(approval.WorkspaceId, approval.ListId.Value, approval.ItemId.Value, ct) is null
                    || provider is null || !await provider.CanDecideAsync(context, ct))
                {
                    return (DecisionResult.InvalidReview, null);
                }
            }

            var schema = approval.InputSchema is null ? null : JsonNode.Parse(approval.InputSchema) as JsonObject;
            var values = WorkflowInputs.WithDefaults(schema, inputs ?? new JsonObject());
            if ((WorkflowInputs.Check(schema, values) ?? await DomainInputs.CheckAsync(schema, values, items, terms, users, ct)) is { } error)
            {
                return (DecisionResult.InvalidInputs, error);
            }

            approval.Inputs = values.ToJsonString();
            approval.Status = outcome == ApprovalOutcomes.Approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
            approval.DecidedBy = userId;
            approval.DecidedAt = time.GetUtcNow();
            approval.Comment = comment;
            var messages = new List<ITenantMessage>();
            var key = WorkflowInterpreter.BookmarkKey(approval.Id);
            if (await db.Bookmarks.FirstOrDefaultAsync(b => b.Kind == BookmarkKinds.Approval && b.Key == key && b.CompletedAt == null, ct) is { } bookmark)
            {
                Complete(bookmark, new JsonObject
                {
                    ["outcome"] = outcome,
                    ["decidedBy"] = userId.ToString(),
                    ["comment"] = comment,
                    ["input"] = values.DeepClone()
                });
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
                    ["input"] = values.DeepClone(),
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
            if (approval.ListId is { } listId && approval.ItemId is { } itemId)
            {
                await activity.RecordAsync(new ItemActivityEntry(approval.WorkspaceId, listId, itemId,
                    ActivityKinds.Approval, summary, $"approval:{approval.Id:N}"), ct);
            }
            return (DecisionResult.Ok, null);
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
        run.Executed = 0;
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

/// <summary>Daily: deletes finished runs older than <see cref="WorkflowOptions.RunRetentionDays"/> with their approvals and bookmarks, old unclaimed completions.</summary>
internal sealed class WorkflowRunCleanupJob(WorkflowsDbContext db, IOptions<WorkflowOptions> options, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "workflows.runCleanup";
    public const string Schedule = "17 3 * * *";
    private const int Batch = 500;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-Math.Max(1, options.Value.RunRetentionDays));

        // Completions no run ever waited for.
        await db.Bookmarks.Where(b => b.RunId == WorkflowBookmarks.Unclaimed && b.CompletedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

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
