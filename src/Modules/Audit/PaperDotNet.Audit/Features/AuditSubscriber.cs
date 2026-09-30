using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Audit.Data;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Audit.Features;

/// <summary>
/// Records list and item events (a Wolverine handler, generated ahead of time). Idempotent: the entry id is the event
/// id, so a redelivered event is recorded once.
/// </summary>
public static class AuditSubscriber
{
    public static Task Handle(ListCreated e, AuditDbContext db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, "list.created", e.ListId, e.ListId, e.Name, cancellationToken);

    public static Task Handle(ListDeleted e, AuditDbContext db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, "list.deleted", e.ListId, e.ListId, e.Name, cancellationToken);

    public static Task Handle(ItemAdded e, AuditDbContext db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, e.IsFolder ? "folder.created" : "item.created", e.ItemId, e.ListId, e.Title, cancellationToken);

    public static Task Handle(ItemUpdated e, AuditDbContext db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, e.IsFolder ? "folder.updated" : "item.updated", e.ItemId, e.ListId, $"{e.Title} ({string.Join(", ", e.ChangedFields)})", cancellationToken);

    public static Task Handle(ItemDeleted e, AuditDbContext db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, e.IsFolder ? "folder.deleted" : "item.deleted", e.ItemId, e.ListId, e.Title, cancellationToken);

    public static Task Handle(ItemRestored e, AuditDbContext db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, "item.restored", e.ItemId, e.ListId, e.Title, cancellationToken);

    public static Task Handle(ItemPurged e, AuditDbContext db, CancellationToken cancellationToken) =>
        RecordAsync(db, e, "item.purged", e.ItemId, e.ListId, e.Title, cancellationToken);

    private static async Task RecordAsync(AuditDbContext database, IntegrationEvent integrationEvent, string action, Guid targetId, Guid? listId, string? summary, CancellationToken cancellationToken)
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
