using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Automation.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;

namespace PaperDotNet.Automation.Features;

/// <summary>An extension trigger was raised (<see cref="IAutomationTriggers"/>); automations with this trigger start.</summary>
public sealed record AutomationTriggerRaised : IntegrationEvent
{
    public required string Trigger { get; init; }

    public required Guid WorkspaceId { get; init; }

    public Guid? ListId { get; init; }

    public Guid? ItemId { get; init; }

    /// <summary>Trigger data as a JSON object.</summary>
    public string? Data { get; init; }
}

internal sealed class AutomationTriggerPublisher(AutomationDbContext db, IOutbox outbox, ITenantContext tenant, ICurrentUser user, EventCausation causation)
    : IAutomationTriggers
{
    public Task RaiseAsync(string triggerKey, Guid workspaceId, AutomationItem? item, JsonObject? data, CancellationToken cancellationToken) =>
        outbox.SaveChangesAsync(db, [new AutomationTriggerRaised
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

/// <summary>Trigger types automations can use: built-in triggers and those of extensions.</summary>
internal sealed class TriggerCatalog(IEnumerable<AutomationTriggerDefinition> extensionTriggers)
{
    public static readonly AutomationTriggerDefinition[] BuiltIn =
    [
        new(AutomationTriggers.Manual, "Started on an item by a person with Contribute access (POST …/items/{id}/automations)."),
        new(AutomationTriggers.ItemAdded, "An item or document was added to a list."),
        new(AutomationTriggers.ItemUpdated, "An item was changed (optionally only when one of changedFields changed)."),
        new(AutomationTriggers.ItemDeleted, "An item was moved to the recycle bin."),
        new(AutomationTriggers.ItemRestored, "An item was restored from the recycle bin."),
    ];

    public IReadOnlyList<AutomationTriggerDefinition> All { get; } = [.. BuiltIn, .. extensionTriggers.OrderBy(t => t.Key, StringComparer.Ordinal)];

    public IReadOnlySet<string> Keys => All.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Starts the automations of a workspace for item events and extension triggers (EVT-07). The trigger's list,
/// content type, changed fields and condition are checked when the event is handled; each matching automation
/// gets a run, saved with the message that starts it (transactional outbox). A run is unique per automation and
/// event, so a redelivered event starts nothing. Changes made by automation carry a higher causation depth; from
/// depth <see cref="MaxDepth"/> on nothing starts, which ends loops such as an automation that updates its own item.
/// </summary>
internal sealed class AutomationTriggerHandler(
    AutomationDbContext db, IListItemStore items, AutomationStarter starter)
    : IEventSubscriber<ItemAdded>, IEventSubscriber<ItemUpdated>, IEventSubscriber<ItemDeleted>, IEventSubscriber<ItemRestored>,
      IEventSubscriber<AutomationTriggerRaised>
{
    public const int MaxDepth = 3;

    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken) =>
        ItemAsync(AutomationTriggers.ItemAdded, integrationEvent, cancellationToken);

    public Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken) =>
        ItemAsync(AutomationTriggers.ItemUpdated, integrationEvent, cancellationToken);

    public Task HandleAsync(ItemDeleted integrationEvent, CancellationToken cancellationToken) =>
        ItemAsync(AutomationTriggers.ItemDeleted, integrationEvent, cancellationToken);

    public Task HandleAsync(ItemRestored integrationEvent, CancellationToken cancellationToken) =>
        ItemAsync(AutomationTriggers.ItemRestored, integrationEvent, cancellationToken);

    public Task HandleAsync(AutomationTriggerRaised integrationEvent, CancellationToken cancellationToken) =>
        StartAsync(integrationEvent.Trigger, integrationEvent, integrationEvent.WorkspaceId, null,
            integrationEvent.ListId is { } listId && integrationEvent.ItemId is { } itemId ? new AutomationItem(integrationEvent.WorkspaceId, listId, itemId) : null,
            integrationEvent.Data, cancellationToken);

    private Task ItemAsync(string trigger, ItemEvent integrationEvent, CancellationToken ct) =>
        integrationEvent.IsFolder
            ? Task.CompletedTask
            : StartAsync(trigger, integrationEvent, integrationEvent.WorkspaceId, integrationEvent,
                new AutomationItem(integrationEvent.WorkspaceId, integrationEvent.ListId, integrationEvent.ItemId), null, ct);

    private async Task StartAsync(
        string trigger, IntegrationEvent source, Guid workspaceId, ItemEvent? itemEvent, AutomationItem? item, string? data, CancellationToken ct)
    {
        if (source.Depth >= MaxDepth)
        {
            return;
        }

        var automations = await db.Automations.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && a.Trigger == trigger && a.Enabled)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
        if (automations.Count == 0)
        {
            return;
        }

        // Redelivered event: its runs were saved together, so they all exist already.
        var ids = automations.Select(a => a.Id).ToList();
        if (await db.Runs.AnyAsync(r => r.EventId == source.EventId && ids.Contains(r.AutomationId), ct))
        {
            return;
        }

        var list = item is null ? null : await items.AsSystem().GetListAsync(item.WorkspaceId, item.ListId, ct);
        var contentType = itemEvent is null ? null : list?.ContentTypes.FirstOrDefault(c => c.Id == itemEvent.ContentTypeId);
        var starts = new List<AutomationStart>();
        foreach (var automation in automations)
        {
            var version = await db.Versions.AsNoTracking().FirstAsync(v => v.AutomationId == automation.Id && v.Number == automation.CurrentVersion, ct);
            var spec = DefinitionJson.Deserialize<AutomationSpec>(version.Definition);
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

            starts.Add(new AutomationStart(automation, item, source.EventId, data, source.Depth, source.UserId, error));
        }

        await starter.StartAsync(starts, ct);
    }
}
