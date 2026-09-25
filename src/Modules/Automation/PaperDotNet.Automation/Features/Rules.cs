using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Automation.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;

namespace PaperDotNet.Automation.Features;

/// <summary>An extension trigger was raised (<see cref="IAutomationTriggers"/>); rules with this trigger run.</summary>
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

/// <summary>Trigger types rules can use: built-in item triggers and those of extensions.</summary>
internal sealed class TriggerCatalog(IEnumerable<AutomationTriggerDefinition> extensionTriggers)
{
    public static readonly AutomationTriggerDefinition[] BuiltIn =
    [
        new(AutomationTriggers.ItemAdded, "An item or document was added to a list."),
        new(AutomationTriggers.ItemUpdated, "An item was changed (optionally only when one of changedFields changed)."),
        new(AutomationTriggers.ItemDeleted, "An item was moved to the recycle bin."),
    ];

    public IReadOnlyList<AutomationTriggerDefinition> All { get; } = [.. BuiltIn, .. extensionTriggers.OrderBy(t => t.Key, StringComparer.Ordinal)];

    public IReadOnlySet<string> Keys => All.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Runs the rules of a workspace for item events and extension triggers (EVT-07). Each rule runs at most
/// once per event (a run row is written first); actions run as the organization, in order, and stop at the
/// first failure. Changes made by automation carry a higher causation depth; from depth 3 on rules no longer
/// react, which ends loops such as a rule that updates the item it reacts to.
/// </summary>
internal sealed partial class RuleRunner(
    AutomationDbContext db, IListItemStore items, ITenantScopeFactory scopes, ITenantContext tenant, TimeProvider time, ILogger<RuleRunner> logger)
    : IEventSubscriber<ItemAdded>, IEventSubscriber<ItemUpdated>, IEventSubscriber<ItemDeleted>, IEventSubscriber<AutomationTriggerRaised>
{
    public const int MaxDepth = 3;

    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken) =>
        integrationEvent.IsFolder ? Task.CompletedTask : RunAsync(AutomationTriggers.ItemAdded, integrationEvent, integrationEvent.WorkspaceId, integrationEvent, null, cancellationToken);

    public Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken) =>
        integrationEvent.IsFolder ? Task.CompletedTask : RunAsync(AutomationTriggers.ItemUpdated, integrationEvent, integrationEvent.WorkspaceId, integrationEvent, null, cancellationToken);

    public Task HandleAsync(ItemDeleted integrationEvent, CancellationToken cancellationToken) =>
        integrationEvent.IsFolder ? Task.CompletedTask : RunAsync(AutomationTriggers.ItemDeleted, integrationEvent, integrationEvent.WorkspaceId, integrationEvent, null, cancellationToken);

    public Task HandleAsync(AutomationTriggerRaised integrationEvent, CancellationToken cancellationToken) =>
        RunAsync(integrationEvent.Trigger, integrationEvent, integrationEvent.WorkspaceId, null,
            integrationEvent.ListId is { } listId && integrationEvent.ItemId is { } itemId ? new AutomationItem(integrationEvent.WorkspaceId, listId, itemId) : null,
            cancellationToken, integrationEvent.Data is { } data ? JsonNode.Parse(data) as JsonObject : null);

    private async Task RunAsync(
        string trigger, IntegrationEvent source, Guid workspaceId, ItemEvent? itemEvent, AutomationItem? triggerItem, CancellationToken ct, JsonObject? data = null)
    {
        if (source.Depth >= MaxDepth)
        {
            return;
        }

        var rules = await db.Rules.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.Trigger == trigger && r.Enabled).OrderBy(r => r.Name).ToListAsync(ct);
        if (rules.Count == 0)
        {
            return;
        }

        var item = itemEvent is null ? triggerItem : new AutomationItem(itemEvent.WorkspaceId, itemEvent.ListId, itemEvent.ItemId);
        var list = item is null ? null : await items.AsSystem().GetListAsync(item.WorkspaceId, item.ListId, ct);
        var contentType = itemEvent is null ? null : list?.ContentTypes.FirstOrDefault(c => c.Id == itemEvent.ContentTypeId);
        foreach (var rule in rules)
        {
            var definition = DefinitionJson.Deserialize<RuleDefinition>(rule.Definition);
            if ((definition.Trigger.List is { } listName && list?.Name != listName)
                || (definition.Trigger.ContentType is { } type && contentType?.Name != type && contentType?.Key != type)
                || (definition.Trigger.ChangedFields is { Count: > 0 } fields && itemEvent is not null && !fields.Intersect(itemEvent.ChangedFields).Any()))
            {
                continue;
            }

            var run = new RuleRun { Id = Ids.New(), RuleId = rule.Id, EventId = source.EventId, ItemId = item?.ItemId, Status = RunStatus.Running, StartedAt = time.GetUtcNow() };
            db.RuleRuns.Add(run);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Already handled (the event was delivered again).
                db.ChangeTracker.Clear();
                continue;
            }

            await ExecuteAsync(rule, definition, run, source, item, data, ct);
            run.CompletedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ExecuteAsync(AutomationRule rule, RuleDefinition definition, RuleRun run, IntegrationEvent source, AutomationItem? item, JsonObject? data, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(definition.Condition))
        {
            if (item is null)
            {
                run.Status = RunStatus.Failed;
                run.Error = "The trigger has no item for the condition.";
                return;
            }

            var (matches, error) = await items.AsSystem().QueryAsync(
                item.WorkspaceId, item.ListId, new ListItemQuery($"id eq {item.ItemId} and ({definition.Condition})", Top: 1), ct);
            if (error is not null || matches.Count == 0)
            {
                run.Status = error is null ? RunStatus.Skipped : RunStatus.Failed;
                run.Error = error;
                return;
            }
        }

        await using var scope = scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!);
        scope.ServiceProvider.GetRequiredService<EventCausation>().Depth = source.Depth + 1;
        var executor = scope.ServiceProvider.GetRequiredService<ActionExecutor>();
        var outcomes = new Dictionary<string, string>();
        foreach (var (action, index) in definition.Actions.Select((a, i) => (a, i)))
        {
            var result = await executor.ExecuteAsync(action, rule.WorkspaceId, item, source.UserId, data, outcomes, $"rule:{rule.Name}", $"rule:{run.Id:N}:{index}", ct);
            if (!result.Succeeded)
            {
                run.Status = RunStatus.Failed;
                run.Error = Truncate($"actions[{index}] ({action.Type}): {result.Error}");
                LogRuleFailed(rule.Name, run.Error);
                return;
            }
        }

        run.Status = RunStatus.Completed;
    }

    internal static string Truncate(string text) => text.Length > 2000 ? text[..2000] : text;

    [LoggerMessage(Level = LogLevel.Information, Message = "Rule {Rule} failed: {Error}")]
    private partial void LogRuleFailed(string rule, string error);
}
