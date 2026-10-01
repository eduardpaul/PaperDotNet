using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Collaboration.Data;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Collaboration.Features;

/// <summary>Records activity entries; idempotent by deduplication key.</summary>
internal sealed class ItemActivity(CollaborationDbContext db, TimeProvider time) : IItemActivity
{
    private const int MaxKindLength = 100;
    private const int MaxSummaryLength = 1000;

    public Task RecordAsync(ChangeActor actor, ItemActivityEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Kind);
        if (entry.Kind.Length > MaxKindLength)
        {
            throw new ArgumentException($"The kind has more than {MaxKindLength} characters.", nameof(entry));
        }

        return AddAsync(db, Create(actor.TenantId, entry.WorkspaceId, entry.ListId, entry.ItemId, entry.Kind, actor.UserId, entry.Summary, [], entry.DeduplicationKey, time.GetUtcNow()),
            cancellationToken);
    }

    public static ActivityEntry Create(
        Guid tenantId, Guid workspaceId, Guid listId, Guid itemId, string kind, Guid? actor, string? summary, IReadOnlyList<string> changedFields, string? key, DateTimeOffset at) => new()
        {
            Id = Ids.New(),
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            ListId = listId,
            ItemId = itemId,
            Kind = kind,
            ActorId = actor,
            Summary = summary is { Length: > MaxSummaryLength } ? summary[..(MaxSummaryLength - 1)] + "…" : summary,
            ChangedFields = JsonSerializer.Serialize([.. changedFields], CollaborationJson.Default.ListString),
            DeduplicationKey = key,
            At = at,
            AtUnixMs = at.ToUnixTimeMilliseconds(),
        };

    public static async Task AddAsync(CollaborationDbContext database, ActivityEntry entry, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = entry.TenantId;
        var key = entry.DeduplicationKey;
        var ct = cancellationToken;
        if (key is not null && await db.Activity.AnyAsync(a => a.TenantId == tenant && a.DeduplicationKey == key, ct))
        {
            return;
        }

        db.Activity.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (key is not null)
        {
            // Recorded concurrently with the same key: the other one won.
            db.ChangeTracker.Clear();
        }
    }
}

/// <summary>
/// Writes item changes to the timeline (the event id makes it idempotent) and removes the comments and activity of
/// purged items. A Wolverine handler generated ahead of time.
/// </summary>
public static class ActivitySubscriber
{
    public static Task Handle(ItemAdded e, CollaborationDbContext db, CancellationToken cancellationToken) => RecordAsync(db, e, ActivityKinds.Created, cancellationToken);

    public static Task Handle(ItemUpdated e, CollaborationDbContext db, CancellationToken cancellationToken) => RecordAsync(db, e, ActivityKinds.Updated, cancellationToken);

    public static Task Handle(ItemDeleted e, CollaborationDbContext db, CancellationToken cancellationToken) => RecordAsync(db, e, ActivityKinds.Deleted, cancellationToken);

    public static Task Handle(ItemRestored e, CollaborationDbContext db, CancellationToken cancellationToken) => RecordAsync(db, e, ActivityKinds.Restored, cancellationToken);

    public static async Task Handle(ItemPurged e, CollaborationDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = e.TenantId;
        var itemId = e.ItemId;
        var ct = cancellationToken;
        db.Comments.RemoveRange(await db.Comments.Where(c => c.TenantId == tenant && c.ItemId == itemId).ToListAsync(ct));
        db.Activity.RemoveRange(await db.Activity.Where(a => a.TenantId == tenant && a.ItemId == itemId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    private static Task RecordAsync(CollaborationDbContext db, ItemEvent change, string kind, CancellationToken ct) =>
        change.IsFolder
            ? Task.CompletedTask
            : ItemActivity.AddAsync(db, ItemActivity.Create(change.TenantId, change.WorkspaceId, change.ListId, change.ItemId, kind, change.UserId, null,
                change.ChangedFields, $"event:{change.EventId:N}", change.OccurredAt), ct);
}
