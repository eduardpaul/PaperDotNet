using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
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

/// <summary>Starts the workflows whose item triggers match an item event (Wolverine handler, one queue per event type).</summary>
public static class WorkflowTriggerSubscriber
{
    public static Task Handle(ItemCreated e, WorkflowStarter starter, CancellationToken cancellationToken) =>
        starter.OnItemEventAsync(WorkflowTriggers.ItemAdded, e, e.ListId, e.ItemId, [], null, cancellationToken);

    public static Task Handle(ItemUpdated e, WorkflowStarter starter, CancellationToken cancellationToken) =>
        starter.OnItemEventAsync(WorkflowTriggers.ItemUpdated, e, e.ListId, e.ItemId, e.ChangedFields, null, cancellationToken);

    public static Task Handle(ItemDeleted e, WorkflowStarter starter, CancellationToken cancellationToken) =>
        starter.OnItemEventAsync(WorkflowTriggers.ItemDeleted, e, e.ListId, e.ItemId, [], new JsonObject { ["title"] = e.Title }, cancellationToken);
}

/// <summary>Whether an item matches an OData filter (workflow conditions and <c>if</c> nodes).</summary>
public sealed class ItemConditions(IListItemStore items)
{
    public async Task<(bool Matches, string? Error)> MatchesAsync(Guid tenantId, Guid listId, Guid itemId, string filter, CancellationToken cancellationToken)
    {
        var (found, error) = await items.QueryAsync(tenantId, listId, filter, null, 1, itemId, cancellationToken);
        return (found.Count > 0, error);
    }
}

/// <summary>Creates runs and sends them to the interpreter. Queries copy their arguments into locals (ADR-0039).</summary>
public sealed partial class WorkflowStarter(WorkflowsDbContext db, IOutbox outbox, IListItemStore items, ItemConditions conditions, TimeProvider time, ILogger<WorkflowStarter> logger)
{
    /// <summary>Changes caused by this many workflow reactions in a row start no more workflows (loop protection).</summary>
    public const int MaxDepth = 8;

    public async Task OnItemEventAsync(string trigger, IntegrationEvent cause, Guid listId, Guid itemId, IReadOnlyList<string> changedFields, JsonObject? data, CancellationToken cancellationToken)
    {
        if (cause.Depth >= MaxDepth)
        {
            LogTooDeep(trigger, itemId, cause.Depth);
            return;
        }

        var tenant = cause.TenantId;
        var listName = (await items.FindListAsync(tenant, listId, cancellationToken))?.Name;
        var runs = new List<WorkflowRun>();
        foreach (var (workflow, spec) in await EnabledAsync(tenant, trigger, cancellationToken))
        {
            var matching = spec.AllTriggers.Any(t => t.Type == trigger
                && (t.List is null || t.List == listName)
                && (t.ChangedFields is not { Count: > 0 } || t.ChangedFields.Intersect(changedFields, StringComparer.Ordinal).Any()));
            if (!matching)
            {
                continue;
            }

            if (spec.Condition is { Length: > 0 } condition && trigger != WorkflowTriggers.ItemDeleted)
            {
                var (matches, error) = await conditions.MatchesAsync(tenant, listId, itemId, condition, cancellationToken);
                if (error is not null)
                {
                    LogConditionFailed(workflow.Name, error);
                }

                if (!matches)
                {
                    continue;
                }
            }

            // The run's id comes from the event and the workflow, so a redelivered event starts nothing twice.
            var runId = RunIdFor(cause.EventId, workflow.Id);
            if (await RunExistsAsync(tenant, runId, cancellationToken))
            {
                continue;
            }

            runs.Add(NewRun(runId, workflow, spec, trigger, listId, itemId, data, cause.UserId, cause.Depth));
        }

        await StartAsync(runs, cancellationToken);
    }

    /// <summary>A run started by a person, on an item or not, with <paramref name="inputs"/> as variables.</summary>
    public async Task<WorkflowRun> StartManualAsync(WorkflowDefinition workflow, WorkflowSpec spec, Guid? listId, Guid? itemId, JsonObject? inputs, Guid userId, CancellationToken cancellationToken)
    {
        var run = NewRun(Ids.New(), workflow, spec, WorkflowTriggers.Manual, listId, itemId, null, userId, 0);
        if (inputs is not null)
        {
            var variables = JsonNode.Parse(run.Variables)!.AsObject();
            foreach (var (name, value) in inputs)
            {
                variables[name] = value?.DeepClone();
            }

            run.Variables = variables.ToJsonString();
        }

        await StartAsync([run], cancellationToken);
        return run;
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

    private async Task<List<(WorkflowDefinition Workflow, WorkflowSpec Spec)>> EnabledAsync(Guid tenantId, string trigger, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var pattern = $",{trigger},";
        var ct = cancellationToken;
        var workflows = await context.Workflows.Where(w => w.TenantId == tenant && w.Enabled && w.TriggerTypes.Contains(pattern)).ToListAsync(ct);
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
