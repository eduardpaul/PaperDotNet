using System.Text;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Collaboration.Data;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Collaboration.Features;

/// <summary>Records activity entries; idempotent by deduplication key.</summary>
internal sealed class ItemActivity(CollaborationDbContext db, ICurrentUser user, TimeProvider time) : IItemActivity
{
    private const int MaxKindLength = 100;
    private const int MaxSummaryLength = 1000;

    public Task RecordAsync(ItemActivityEntry entry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Kind);
        if (entry.Kind.Length > MaxKindLength)
        {
            throw new ArgumentException($"The kind has more than {MaxKindLength} characters.", nameof(entry));
        }

        return AddAsync(Create(entry.WorkspaceId, entry.ListId, entry.ItemId, entry.Kind, user.UserId, entry.Summary, [], entry.DeduplicationKey, time.GetUtcNow()), cancellationToken);
    }

    public static ActivityEntry Create(
        Guid workspaceId, Guid listId, Guid itemId, string kind, Guid? actor, string? summary, IReadOnlyList<string> changedFields, string? key, DateTimeOffset at) => new()
        {
            Id = Ids.New(),
            WorkspaceId = workspaceId,
            ListId = listId,
            ItemId = itemId,
            Kind = kind,
            ActorId = actor,
            Summary = summary is { Length: > MaxSummaryLength } ? summary[..(MaxSummaryLength - 1)] + "…" : summary,
            ChangedFields = [.. changedFields],
            DeduplicationKey = key,
            At = at,
        };

    public async Task AddAsync(ActivityEntry entry, CancellationToken ct)
    {
        if (entry.DeduplicationKey is { } key && await db.Activity.AnyAsync(a => a.DeduplicationKey == key, ct))
        {
            return;
        }

        db.Activity.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Writes item changes to the timeline (the event id makes it idempotent) and removes data of purged items.</summary>
internal sealed class ItemActivityRecorder(CollaborationDbContext db, ItemActivity activity)
    : IEventSubscriber<ItemAdded>, IEventSubscriber<ItemUpdated>, IEventSubscriber<ItemDeleted>, IEventSubscriber<ItemRestored>, IEventSubscriber<ItemPurged>
{
    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken) => RecordAsync(integrationEvent, ActivityKinds.Created, cancellationToken);

    public Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken) => RecordAsync(integrationEvent, ActivityKinds.Updated, cancellationToken);

    public Task HandleAsync(ItemDeleted integrationEvent, CancellationToken cancellationToken) => RecordAsync(integrationEvent, ActivityKinds.Deleted, cancellationToken);

    public Task HandleAsync(ItemRestored integrationEvent, CancellationToken cancellationToken) => RecordAsync(integrationEvent, ActivityKinds.Restored, cancellationToken);

    public async Task HandleAsync(ItemPurged integrationEvent, CancellationToken cancellationToken)
    {
        await db.Comments.Where(c => c.ItemId == integrationEvent.ItemId).ExecuteDeleteAsync(cancellationToken);
        await db.Activity.Where(a => a.ItemId == integrationEvent.ItemId).ExecuteDeleteAsync(cancellationToken);
    }

    private Task RecordAsync(ItemEvent change, string kind, CancellationToken ct) =>
        activity.AddAsync(
            ItemActivity.Create(change.WorkspaceId, change.ListId, change.ItemId, kind, change.UserId, null, change.ChangedFields, $"event:{change.EventId:N}", change.OccurredAt),
            ct);
}

/// <summary>Makes comments findable: their text is part of the item's search document.</summary>
internal sealed class CommentSearchContent(CollaborationDbContext db) : IItemSearchContributor
{
    private const int MaxCharacters = 20_000;

    public async Task<IReadOnlyDictionary<Guid, ItemSearchContent>> GetContentAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        var comments = await db.Comments.AsNoTracking()
            .Where(c => itemIds.Contains(c.ItemId))
            .OrderBy(c => c.Id)
            .Select(c => new { c.ItemId, c.Text })
            .ToListAsync(cancellationToken);
        return comments.GroupBy(c => c.ItemId).ToDictionary(
            g => g.Key,
            g =>
            {
                var text = new StringBuilder();
                foreach (var comment in g.TakeWhile(_ => text.Length < MaxCharacters))
                {
                    text.AppendLine(comment.Text);
                }

                return new ItemSearchContent(text.ToString(), null);
            });
    }
}
