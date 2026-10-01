using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>Runs (or continues) a workflow run; sent through the outbox when a run starts and when a long run continues.</summary>
public sealed record ResumeRun(Guid TenantId, Guid RunId);

/// <summary>Wolverine handler of <see cref="ResumeRun"/> (generated ahead of time).</summary>
public static class WorkflowRunSubscriber
{
    public static Task Handle(ResumeRun message, WorkflowInterpreter interpreter, CancellationToken cancellationToken) =>
        interpreter.RunAsync(message.TenantId, message.RunId, cancellationToken);
}

/// <summary>
/// Starts the workflows whose item triggers match an item event (Wolverine handler, one queue per event type). Folders
/// start no workflows.
/// </summary>
public static class WorkflowTriggerSubscriber
{
    public static Task Handle(ItemAdded e, WorkflowStarter starter, CancellationToken cancellationToken) =>
        e.IsFolder ? Task.CompletedTask : starter.OnItemEventAsync(WorkflowTriggers.ItemAdded, e, [], null, cancellationToken);

    public static Task Handle(ItemUpdated e, WorkflowStarter starter, CancellationToken cancellationToken) =>
        e.IsFolder ? Task.CompletedTask : starter.OnItemEventAsync(WorkflowTriggers.ItemUpdated, e, e.ChangedFields, null, cancellationToken);

    public static Task Handle(ItemDeleted e, WorkflowStarter starter, CancellationToken cancellationToken) =>
        e.IsFolder ? Task.CompletedTask : starter.OnItemEventAsync(WorkflowTriggers.ItemDeleted, e, [], new JsonObject { ["title"] = e.Title }, cancellationToken);

    public static Task Handle(ItemRestored e, WorkflowStarter starter, CancellationToken cancellationToken) =>
        e.IsFolder ? Task.CompletedTask : starter.OnItemEventAsync(WorkflowTriggers.ItemRestored, e, [], null, cancellationToken);
}

/// <summary>
/// Starts the workflows whose trigger is a raised event: workflow events (<c>wf.{key}.{event}</c>) and module or extension
/// triggers (Wolverine handler, generated ahead of time).
/// </summary>
public static class WorkflowEventSubscriber
{
    public static Task Handle(WorkflowTriggerRaised e, WorkflowStarter starter, CancellationToken cancellationToken) =>
        starter.OnTriggerAsync(e, cancellationToken);
}

/// <summary>Whether an item matches an OData filter (workflow conditions and <c>if</c> nodes).</summary>
public sealed class ItemConditions(WorkflowItems items)
{
    public async Task<(bool Matches, string? Error)> MatchesAsync(Guid tenantId, Guid workspaceId, Guid listId, Guid itemId, string filter, CancellationToken cancellationToken)
    {
        var (found, error) = await items.QueryAsync(new ChangeActor(tenantId, null), workspaceId, listId, filter, null, 1, itemId, cancellationToken);
        return (found.Count > 0, error);
    }
}

/// <summary>
/// Creates runs and sends them to the interpreter. A trigger of the event's type matches when its list, content type,
/// changed fields, terms and data fit; then the workflow's condition is checked on the item. A run's id comes from the
/// event and the workflow, so a redelivered event starts nothing twice. Changes made by workflows carry a higher causation
/// depth; from <see cref="MaxDepth"/> on nothing starts (loop protection). Queries copy their arguments into locals (ADR-0039).
/// </summary>
public sealed partial class WorkflowStarter(
    WorkflowsDbContext db, IOutbox outbox, WorkflowItems items, ItemConditions conditions, TriggerTerms terms, BuiltInWorkflows builtIns, TimeProvider time,
    ILogger<WorkflowStarter> logger)
{
    /// <summary>Changes caused by this many workflow reactions in a row start no more workflows (loop protection).</summary>
    public const int MaxDepth = 8;

    /// <summary>Most items one manual start may select.</summary>
    public const int MaxItemsPerStart = 100;

    /// <summary>What a trigger is matched against.</summary>
    private sealed record Cause(
        Guid TenantId, Guid WorkspaceId, string Trigger, Guid EventId, Guid? ListId, Guid? ItemId, Guid? ContentTypeId,
        IReadOnlyList<string> ChangedFields, JsonObject? Data, Guid? UserId, int Depth);

    public Task OnItemEventAsync(string trigger, ItemEvent cause, IReadOnlyList<string> changedFields, JsonObject? data, CancellationToken cancellationToken) =>
        StartMatchingAsync(new Cause(cause.TenantId, cause.WorkspaceId, trigger, cause.EventId, cause.ListId, cause.ItemId, cause.ContentTypeId,
            changedFields, data, cause.UserId, cause.Depth), cancellationToken);

    /// <summary>Starts the enabled workflows of the workspace whose triggers have the event's type and fit it.</summary>
    public Task OnTriggerAsync(WorkflowTriggerRaised cause, CancellationToken cancellationToken) =>
        StartMatchingAsync(new Cause(cause.TenantId, cause.WorkspaceId, cause.Trigger, cause.EventId, cause.ListId, cause.ItemId, null,
            [], cause.Data is { } json ? JsonNode.Parse(json) as JsonObject : null, cause.UserId, cause.Depth), cancellationToken);

    private async Task StartMatchingAsync(Cause cause, CancellationToken cancellationToken)
    {
        if (cause.Depth >= MaxDepth)
        {
            LogTooDeep(cause.Trigger, cause.ItemId ?? Guid.Empty, cause.Depth);
            return;
        }

        var tenant = cause.TenantId;

        // A deleted item's list is found too while the list itself is not deleted. A library's built-in workflows that are on
        // by default are created before its events are matched.
        var reader = new ChangeActor(tenant, null);
        var list = cause.ListId is { } listId ? await items.FindListAsync(reader, cause.WorkspaceId, listId, cancellationToken) : null;
        if (list is { IsLibrary: true })
        {
            await builtIns.EnsureDefaultsAsync(tenant, list, cancellationToken);
        }

        // A per-library workflow only starts for its library's items.
        var enabled = (await EnabledAsync(tenant, cause.WorkspaceId, cause.Trigger, cancellationToken))
            .Where(w => w.Workflow.ListId is null || w.Workflow.ListId == cause.ListId).ToList();
        if (enabled.Count == 0)
        {
            return;
        }

        // The item's values, read once when a content type (of a raised trigger) or terms have to be checked.
        ListItemData? current = null;
        var loaded = false;
        async Task<ListItemData?> CurrentAsync()
        {
            if (!loaded && cause is { ListId: { } itemList, ItemId: { } itemId })
            {
                current = await items.GetAsync(reader, cause.WorkspaceId, itemList, itemId, cancellationToken);
                loaded = true;
            }

            return current;
        }

        async Task<bool> MatchesAsync(WorkflowTrigger candidate)
        {
            if ((candidate.List is { } listName && list?.Name != listName)
                || (candidate.ChangedFields is { Count: > 0 } fields && !fields.Intersect(cause.ChangedFields, StringComparer.Ordinal).Any())
                || !candidate.MatchesData(cause.Data))
            {
                return false;
            }

            if (candidate.ContentType is { } type)
            {
                var contentTypeId = cause.ContentTypeId ?? (await CurrentAsync())?.ContentTypeId;
                var contentType = list?.ContentTypes.FirstOrDefault(c => c.Id == contentTypeId);
                if (contentType?.Name != type && contentType?.Key != type)
                {
                    return false;
                }
            }

            return candidate.Terms is not { Count: > 0 } wanted || await terms.HasAnyAsync(tenant, await CurrentAsync(), wanted, cancellationToken);
        }

        var runs = new List<WorkflowRun>();
        foreach (var (workflow, spec) in enabled)
        {
            var matched = false;
            foreach (var candidate in spec.AllTriggers.Where(t => t.Type == cause.Trigger))
            {
                if (await MatchesAsync(candidate))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            if (spec.Condition is { Length: > 0 } condition && cause.Trigger != WorkflowTriggers.ItemDeleted && cause is { ListId: { } conditionList, ItemId: { } conditionItem })
            {
                var (matches, error) = await conditions.MatchesAsync(tenant, cause.WorkspaceId, conditionList, conditionItem, condition, cancellationToken);
                if (error is not null)
                {
                    LogConditionFailed(workflow.Name, error);
                }

                if (!matches)
                {
                    continue;
                }
            }

            var runId = RunIdFor(cause.EventId, workflow.Id);
            if (await RunExistsAsync(tenant, runId, cancellationToken))
            {
                continue;
            }

            runs.Add(NewRun(runId, workflow, spec, cause.Trigger, cause.ListId, cause.ItemId, cause.Data, cause.UserId, cause.Depth));
        }

        await StartAsync(runs, cancellationToken);
    }

    /// <summary>A run of a timed trigger (an occurrence, or an item's date): once per <paramref name="eventId"/>.</summary>
    public async Task StartTimedAsync(
        WorkflowDefinition workflow, WorkflowSpec spec, string trigger, Guid eventId, Guid? itemId, JsonObject data, CancellationToken cancellationToken, Guid? listId = null)
    {
        var runId = RunIdFor(eventId, workflow.Id);
        if (!await RunExistsAsync(workflow.TenantId, runId, cancellationToken))
        {
            await StartAsync([NewRun(runId, workflow, spec, trigger, itemId is null ? null : listId, itemId, data, null, 0)], cancellationToken);
        }
    }

    /// <summary>A run started by a person, on an item or not, with <paramref name="inputs"/> as variables.</summary>
    public async Task<WorkflowRun> StartManualAsync(WorkflowDefinition workflow, WorkflowSpec spec, Guid? listId, Guid? itemId, JsonObject? inputs, Guid userId, CancellationToken cancellationToken) =>
        (await StartManualAsync(workflow, spec, listId, itemId is { } id ? [id] : [null], inputs, userId, cancellationToken))[0];

    /// <summary>Runs started by a person: one per selected item (or one without an item).</summary>
    public async Task<List<WorkflowRun>> StartManualAsync(
        WorkflowDefinition workflow, WorkflowSpec spec, Guid? listId, IReadOnlyList<Guid?> itemIds, JsonObject? inputs, Guid userId, CancellationToken cancellationToken)
    {
        var runs = new List<WorkflowRun>();
        foreach (var itemId in itemIds)
        {
            var run = NewRun(Ids.New(), workflow, spec, WorkflowTriggers.Manual, itemId is null ? null : listId, itemId, null, userId, 0);
            if (inputs is not null)
            {
                var variables = JsonNode.Parse(run.Variables)!.AsObject();
                foreach (var (name, value) in inputs)
                {
                    variables[name] = value?.DeepClone();
                }

                run.Variables = variables.ToJsonString();
            }

            runs.Add(run);
        }

        await StartAsync(runs, cancellationToken);
        return runs;
    }

    private async Task StartAsync(List<WorkflowRun> runs, CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
        {
            return;
        }

        db.WorkflowRuns.AddRange(runs);
        await outbox.SaveChangesAsync(db, [], [.. runs.Select(r => (object)new ResumeRun(r.TenantId, r.Id))], cancellationToken);
    }

    private WorkflowRun NewRun(Guid id, WorkflowDefinition workflow, WorkflowSpec spec, string trigger, Guid? listId, Guid? itemId, JsonObject? data, Guid? userId, int depth) => new()
    {
        Id = id,
        TenantId = workflow.TenantId,
        WorkspaceId = workflow.WorkspaceId,
        WorkflowId = workflow.Id,
        WorkflowVersion = workflow.CurrentVersion,
        Status = RunStatus.Running,
        Trigger = trigger,
        ListId = listId,
        ItemId = itemId,
        Data = data?.ToJsonString(),
        Variables = (spec.Variables ?? []).ToJsonString(),
        StartedBy = userId,
        Depth = depth,
        StartedAt = time.GetUtcNow(),
    };

    private async Task<List<(WorkflowDefinition Workflow, WorkflowSpec Spec)>> EnabledAsync(Guid tenantId, Guid workspaceId, string trigger, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var workspace = workspaceId;
        var pattern = $",{trigger},";
        var ct = cancellationToken;
        var workflows = await context.Workflows.Where(w => w.TenantId == tenant && w.WorkspaceId == workspace && w.Enabled && w.TriggerTypes.Contains(pattern)).ToListAsync(ct);
        var result = new List<(WorkflowDefinition, WorkflowSpec)>();
        foreach (var workflow in workflows)
        {
            if (await WorkflowVersions.FindAsync(context, tenant, workflow.Id, workflow.CurrentVersion, ct) is { } version)
            {
                result.Add((workflow, WorkflowJson.Deserialize(version.Definition)));
            }
        }

        return result;
    }

    private Task<bool> RunExistsAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var id = runId;
        var ct = cancellationToken;
        return context.WorkflowRuns.AnyAsync(r => r.TenantId == tenant && r.Id == id, ct);
    }

    /// <summary>A stable id made from a text (e.g. the run and what raised an event): the same text, the same id.</summary>
    internal static Guid StableId(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    internal static Guid RunIdFor(Guid eventId, Guid workflowId)
    {
        Span<byte> input = stackalloc byte[32];
        eventId.TryWriteBytes(input);
        workflowId.TryWriteBytes(input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not starting {Trigger} workflows for item {ItemId}: the change comes from {Depth} workflow reactions in a row.")]
    private partial void LogTooDeep(string trigger, Guid itemId, int depth);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The condition of the workflow {Workflow} cannot be checked: {Error}")]
    private partial void LogConditionFailed(string workflow, string error);
}

internal static class WorkflowVersions
{
    public static Task<WorkflowVersion?> FindAsync(WorkflowsDbContext database, Guid tenantId, Guid workflowId, int number, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var workflow = workflowId;
        var version = number;
        var ct = cancellationToken;
        return db.WorkflowVersions.Where(v => v.TenantId == tenant && v.WorkflowId == workflow && v.Number == version).FirstOrDefaultAsync(ct);
    }
}
