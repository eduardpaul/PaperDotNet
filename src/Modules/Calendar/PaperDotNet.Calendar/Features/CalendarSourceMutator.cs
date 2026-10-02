using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Calendar.Features;

/// <summary>Scoped to an authorized source refresh, not a caller-supplied bypass.</summary>
internal sealed class CalendarImportScope
{
    public bool Active { get; set; }
}

internal sealed class CalendarSourceMutator(CalendarDbContext db, CalendarImportScope scope) : IItemMutator
{
    internal static readonly string[] Fields = ["title", "start", "end", "allDay", "location", "description"];
    public int Sequence => 100;
    public bool AppliesTo(ItemEventScope scope) => !scope.IsFolder && scope.ContentTypeKey == CalendarService.EventKey;

    public async ValueTask ItemUpdatingAsync(ItemMutationContext context, CancellationToken cancellationToken)
    {
        if (scope.Active || !await IsManagedAsync(context.ItemId, cancellationToken))
        {
            return;
        }

        if (Fields.Any(f => !JsonNode.DeepEquals(context.Before?[f], context.After?[f])))
        {
            context.Cancel("This event is replicated from a calendar source. Edit its calendar fields in the source calendar.");
        }
    }

    public async ValueTask ItemDeletingAsync(ItemMutationContext context, CancellationToken cancellationToken)
    {
        if (!scope.Active && await IsManagedAsync(context.ItemId, cancellationToken))
        {
            context.Cancel("Remove this event in the source calendar, or remove the source subscription first.");
        }
    }

    public Task<bool> IsManagedAsync(Guid itemId, CancellationToken ct) => db.Sources.AnyAsync(s => s.ItemId == itemId
        && s.SubscriptionId != null && db.Subscriptions.Any(p => p.Id == s.SubscriptionId), ct);
}
