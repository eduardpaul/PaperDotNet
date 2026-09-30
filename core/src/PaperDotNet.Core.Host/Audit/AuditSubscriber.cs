using Microsoft.EntityFrameworkCore;
using PaperDotNet.Core.Host.Data;
using PaperDotNet.Core.Messaging;

namespace PaperDotNet.Core.Host.Audit;

/// <summary>
/// Records list and item events (a Wolverine handler, generated ahead of time). Idempotent: the entry id is the event
/// id, so a redelivered event is recorded once.
/// </summary>
public static class AuditSubscriber
{
    public static Task Handle(ListCreated e, CoreDb db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, "list.created", e.ListId, e.ListId, e.Name, cancellationToken);

    public static Task Handle(ListDeleted e, CoreDb db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, "list.deleted", e.ListId, e.ListId, e.Name, cancellationToken);

    public static Task Handle(ItemCreated e, CoreDb db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, "item.created", e.ItemId, e.ListId, e.Title, cancellationToken);

    public static Task Handle(ItemUpdated e, CoreDb db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, "item.updated", e.ItemId, e.ListId, $"{e.Title} ({string.Join(", ", e.ChangedFields)})", cancellationToken);

    public static Task Handle(ItemDeleted e, CoreDb db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, "item.deleted", e.ItemId, e.ListId, e.Title, cancellationToken);

    private static async Task RecordAsync(CoreDb database, IntegrationEvent integrationEvent, string action, Guid targetId, Guid? listId, string? summary, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = integrationEvent.TenantId;
        var id = integrationEvent.EventId;
        var ct = cancellationToken;
        if (await db.AuditEntries.AnyAsync(a => a.TenantId == tenant && a.Id == id, ct))
        {
            return;
        }

        db.AuditEntries.Add(new AuditEntry
        {
            Id = id,
            TenantId = tenant,
            UserId = integrationEvent.UserId,
            Action = action,
            TargetId = targetId,
            ListId = listId,
            Summary = summary is { Length: > 500 } ? summary[..500] : summary,
            OccurredAt = integrationEvent.OccurredAt,
        });
        await db.SaveChangesAsync(ct);
    }
}
