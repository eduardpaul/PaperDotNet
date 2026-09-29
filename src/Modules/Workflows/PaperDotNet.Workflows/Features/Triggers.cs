using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>An extension trigger was raised (<see cref="IWorkflowTriggers"/>); workflows with this trigger start.</summary>
public sealed record WorkflowTriggerRaised : IntegrationEvent
{
    public required string Trigger { get; init; }

    public required Guid WorkspaceId { get; init; }

    public Guid? ListId { get; init; }

    public Guid? ItemId { get; init; }

    /// <summary>Trigger data as a JSON object.</summary>
    public string? Data { get; init; }
}

internal sealed class WorkflowTriggerPublisher(WorkflowsDbContext db, IOutbox outbox, ITenantContext tenant, ICurrentUser user, EventCausation causation)
    : IWorkflowTriggers
{
    public Task RaiseAsync(string triggerKey, Guid workspaceId, WorkflowItem? item, JsonObject? data, CancellationToken cancellationToken) =>
        outbox.SaveChangesAsync(db, [new WorkflowTriggerRaised
        {
            TenantId = tenant.TenantId!.Value,
            TenantIdentifier = tenant.TenantIdentifier!,
            UserId = user.UserId,
            Depth = causation.Depth,
            Trigger = triggerKey,
            WorkspaceId = workspaceId,
            ListId = item?.ListId,
            ItemId = item?.ItemId,
            Data = data?.ToJsonString(),
        }], cancellationToken: cancellationToken);
}

/// <summary>Trigger types workflows can use: built-in triggers and those of extensions.</summary>
internal sealed class TriggerCatalog(IEnumerable<WorkflowTriggerDefinition> extensionTriggers)
{
    public static readonly WorkflowTriggerDefinition[] BuiltIn =
    [
        new(WorkflowTriggers.Manual, "Started on an item by a person with Contribute access (POST …/items/{id}/workflows)."),
        new(WorkflowTriggers.ItemAdded, "An item or document was added to a list."),
        new(WorkflowTriggers.ItemUpdated, "An item was changed (optionally only when one of changedFields changed)."),
        new(WorkflowTriggers.ItemDeleted, "An item was moved to the recycle bin."),
        new(WorkflowTriggers.ItemRestored, "An item was restored from the recycle bin."),
    ];

    public IReadOnlyList<WorkflowTriggerDefinition> All { get; } = [.. BuiltIn, .. extensionTriggers.OrderBy(t => t.Key, StringComparer.Ordinal)];

    public IReadOnlySet<string> Keys => All.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Starts the workflows of a workspace for item events and extension triggers (EVT-07). The trigger's list,
/// content type, changed fields and condition are checked when the event is handled; each matching workflow
/// gets a run, saved with the message that starts it (transactional outbox). A run is unique per workflow and
/// event, so a redelivered event starts nothing. Changes made by workflow carry a higher causation depth; from
/// depth <see cref="MaxDepth"/> on nothing starts, which ends loops such as a workflow that updates its own item.
/// </summary>
internal sealed class WorkflowTriggerHandler(
    WorkflowsDbContext db, IListItemStore items, WorkflowStarter starter)
    : IEventSubscriber<ItemAdded>, IEventSubscriber<ItemUpdated>, IEventSubscriber<ItemDeleted>, IEventSubscriber<ItemRestored>,
      IEventSubscriber<WorkflowTriggerRaised>
{
    public const int MaxDepth = 3;

    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken) =>
        ItemAsync(WorkflowTriggers.ItemAdded, integrationEvent, cancellationToken);

    public Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken) =>
        ItemAsync(WorkflowTriggers.ItemUpdated, integrationEvent, cancellationToken);

    public Task HandleAsync(ItemDeleted integrationEvent, CancellationToken cancellationToken) =>
        ItemAsync(WorkflowTriggers.ItemDeleted, integrationEvent, cancellationToken);

    public Task HandleAsync(ItemRestored integrationEvent, CancellationToken cancellationToken) =>
        ItemAsync(WorkflowTriggers.ItemRestored, integrationEvent, cancellationToken);

    public Task HandleAsync(WorkflowTriggerRaised integrationEvent, CancellationToken cancellationToken) =>
        StartAsync(integrationEvent.Trigger, integrationEvent, integrationEvent.WorkspaceId, null,
            integrationEvent.ListId is { } listId && integrationEvent.ItemId is { } itemId ? new WorkflowItem(integrationEvent.WorkspaceId, listId, itemId) : null,
            integrationEvent.Data, cancellationToken);

    private Task ItemAsync(string trigger, ItemEvent integrationEvent, CancellationToken ct) =>
        integrationEvent.IsFolder
            ? Task.CompletedTask
            : StartAsync(trigger, integrationEvent, integrationEvent.WorkspaceId, integrationEvent,
                new WorkflowItem(integrationEvent.WorkspaceId, integrationEvent.ListId, integrationEvent.ItemId), null, ct);

    private async Task StartAsync(
        string trigger, IntegrationEvent source, Guid workspaceId, ItemEvent? itemEvent, WorkflowItem? item, string? data, CancellationToken ct)
    {
        if (source.Depth >= MaxDepth)
        {
            return;
        }

        var workflows = await db.Workflows.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && a.Trigger == trigger && a.Enabled)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
        if (workflows.Count == 0)
        {
            return;
        }

        // Redelivered event: its runs were saved together, so they all exist already.
        var ids = workflows.Select(a => a.Id).ToList();
        if (await db.Runs.AnyAsync(r => r.EventId == source.EventId && ids.Contains(r.WorkflowId), ct))
        {
            return;
        }

        var list = item is null ? null : await items.AsSystem().GetListAsync(item.WorkspaceId, item.ListId, ct);
        var contentType = itemEvent is null ? null : list?.ContentTypes.FirstOrDefault(c => c.Id == itemEvent.ContentTypeId);
        var starts = new List<WorkflowStart>();
        foreach (var workflow in workflows)
        {
            var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == workflow.Id && v.Number == workflow.CurrentVersion, ct);
            var spec = DefinitionJson.Deserialize<WorkflowSpec>(version.Definition);
            if ((spec.Trigger.List is { } listName && list?.Name != listName)
                || (spec.Trigger.ContentType is { } type && contentType?.Name != type && contentType?.Key != type)
                || (spec.Trigger.ChangedFields is { Count: > 0 } fields && itemEvent is not null && !fields.Intersect(itemEvent.ChangedFields).Any()))
            {
                continue;
            }

            // A condition that cannot be checked (e.g. a field was removed) gives a failed run, so it is visible.
            var (matches, error) = spec.Condition is { } condition && item is not null ? await starter.CheckConditionAsync(item, condition, ct) : (true, null);
            if (!matches && error is null)
            {
                continue;
            }

            starts.Add(new WorkflowStart(workflow, item, source.EventId, data, source.Depth, source.UserId, error));
        }

        await starter.StartAsync(starts, ct);
    }
}
