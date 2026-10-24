using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Content;

public sealed partial class ContentStore
{
    public Task<PublicationsRow> PublishAsync(string actor, string itemId, long expectedVersion, CancellationToken cancellationToken = default) =>
        WriteAsync(itemId, async ct =>
        {
            var item = await RequireAsync(actor, itemId, "publish", ct);
            CheckVersion(item, expectedVersion);
            var collection = await db.Resources.SingleAsync(x => x.Id == item.ContainerId, ct);
            if (item.Kind != "item" || !collection.PublishingEnabled || item.HeadRevisionId is null)
                throw new InvalidOperationException("Explicit publishing requires an item in a publishing collection.");
            var revision = await db.ItemRevisions.SingleAsync(x => x.Id == item.HeadRevisionId, ct);
            if (collection.Kind == "library" && revision.BlobId is null)
                throw new InvalidOperationException("Library items need a blob before explicit publishing.");
            if (item.PublishedRevisionId == revision.Id) throw new InvalidOperationException("This revision is already published.");
            var publication = await PublishRevisionAsync(actor, item, revision, true, ct);
            await AuditAsync(actor, item, "item.publish", new { revision_id = revision.Id, publication_id = publication.Id }, ct);
            return publication;
        }, cancellationToken);

    private async Task<PublicationsRow> PublishRevisionAsync(string actor, ResourcesRow item, ItemRevisionsRow revision, bool explicitPublication, CancellationToken ct)
    {
        // Project using the revision's schema; later required fields must not invalidate a saved draft.
        await ProjectAsync(item, revision, "published", ct);
        var snapshot = new Dictionary<string, object?>
        {
            ["id"] = item.Id,
            ["name"] = revision.Name,
            ["keywords"] = Decode<JsonElement>(revision.Keywords),
            ["values"] = Decode<JsonElement>(revision.Payload),
            ["revision_id"] = revision.Id,
            ["revision_number"] = revision.RevisionNumber,
            ["schema_revision_id"] = revision.SchemaRevisionId,
            ["filename"] = revision.Filename,
        };
        if (revision.BlobId is not null) snapshot["blob_id"] = revision.BlobId;
        var publication = new PublicationsRow
        {
            Id = NewId(),
            CreatedAt = Now,
            ItemId = item.Id,
            RevisionId = revision.Id,
            Version = item.Version,
            Action = "publish",
            PublishedBy = actor,
            Snapshot = Encode(snapshot)
        };
        db.Publications.Add(publication);
        item.PublishedRevisionId = revision.Id;
        item.UpdatedAt = Now;
        if (explicitPublication) item.Version++;
        await SignalAsync(actor, item, "item.published", new { revision_id = revision.Id, revision_number = revision.RevisionNumber, publication_id = publication.Id }, explicitPublication, ct);
        return publication;
    }

    public Task<ResourcesRow> UnpublishAsync(string actor, string itemId, long expectedVersion, CancellationToken cancellationToken = default) =>
        WriteAsync(itemId, async ct =>
        {
            var item = await RequireAsync(actor, itemId, "publish", ct);
            CheckVersion(item, expectedVersion);
            var collection = await db.Resources.SingleAsync(x => x.Id == item.ContainerId, ct);
            if (item.Kind != "item" || !collection.PublishingEnabled || item.PublishedRevisionId is null)
                throw new InvalidOperationException("Item must be explicitly published before it can be unpublished.");
            var revisionId = item.PublishedRevisionId;
            db.Publications.Add(new PublicationsRow
            {
                Id = NewId(),
                CreatedAt = Now,
                ItemId = item.Id,
                RevisionId = revisionId,
                Version = item.Version,
                Action = "unpublish",
                PublishedBy = actor,
                Snapshot = Encode(new { revision_id = revisionId })
            });
            await RemoveSurfaceAsync(item.Id, "published", ct);
            item.PublishedRevisionId = null;
            item.UpdatedAt = Now;
            item.Version++;
            await SyncBusinessKeysAsync(item, ct);
            await SignalAsync(actor, item, "item.unpublished", new { revision_id = revisionId }, true, ct);
            await AuditAsync(actor, item, "item.unpublish", new { revision_id = revisionId }, ct);
            return item;
        }, cancellationToken);

    public Task<ResourcesRow> SetArchivedAsync(string actor, string itemId, long expectedVersion, bool archived,
        CancellationToken cancellationToken = default) => WriteAsync(itemId, async ct =>
        {
            var item = await RequireAsync(actor, itemId, "write", ct);
            CheckVersion(item, expectedVersion);
            if (item.Kind != "item") throw new InvalidOperationException("Only live items can be archived.");
            if ((item.ArchivedAt is not null) == archived) return item;
            item.ArchivedAt = archived ? Now : null;
            item.ArchivedBy = archived ? actor : null;
            item.Version++;
            var action = archived ? "item.archive" : "item.unarchive";
            await SignalAsync(actor, item, archived ? "item.archived" : "item.unarchived", new { archived }, true, ct);
            await AuditAsync(actor, item, action, new { archived }, ct);
            return item;
        }, cancellationToken);

    public Task<ResourcesRow> ConfigureCollectionAsync(string actor, string collectionId, long expectedVersion,
        bool publishingEnabled, string versioning, long maxVersions, CancellationToken cancellationToken = default) =>
        WriteAsync(collectionId, async ct =>
        {
            ValidateHistory(versioning, maxVersions);
            var collection = await RequireAsync(actor, collectionId, "manage", ct);
            CheckVersion(collection, expectedVersion);
            RequireCollection(collection);
            if (collection.PublishingEnabled != publishingEnabled)
            {
                var items = await db.Resources.Where(x => x.ContainerId == collection.Id && x.DeletedAt == null && x.HeadRevisionId != null).ToListAsync(ct);
                foreach (var item in items)
                {
                    var revision = await db.ItemRevisions.SingleAsync(x => x.Id == item.HeadRevisionId, ct);
                    if (publishingEnabled) await ProjectAsync(item, revision, "head", ct);
                    else
                    {
                        if (item.PublishedRevisionId != revision.Id)
                        {
                            if (item.Kind == "folder")
                            {
                                await ProjectAsync(item, revision, "published", ct);
                                item.PublishedRevisionId = revision.Id;
                            }
                            else await PublishRevisionAsync(actor, item, revision, true, ct);
                        }
                        await RemoveSurfaceAsync(item.Id, "head", ct);
                        await SyncBusinessKeysAsync(item, ct);
                    }
                }
            }
            collection.PublishingEnabled = publishingEnabled;
            collection.Versioning = versioning;
            collection.MaxVersions = maxVersions;
            collection.Version++;
            collection.UpdatedAt = Now;
            collection.UpdatedBy = actor;
            // Disabling history retains recorded versions. A smaller bound trims only future retained writes.
            return collection;
        }, cancellationToken);

    private async Task RetainHistoryAsync(ResourcesRow item, ResourcesRow collection, ItemRevisionsRow revision, ItemRevisionsRow? previous, CancellationToken ct)
    {
        var currentValues = Decode<Dictionary<string, JsonElement>>(revision.Payload);
        var oldValues = previous is null ? [] : Decode<Dictionary<string, JsonElement>>(previous.Payload);
        var changed = currentValues.Keys.Union(oldValues.Keys, StringComparer.Ordinal).Where(key =>
            !currentValues.TryGetValue(key, out var value) || !oldValues.TryGetValue(key, out var old) || !JsonElement.DeepEquals(value, old)).ToList();
        if (previous is null || revision.Name != previous.Name) changed.Add("$name");
        if (previous is null || revision.Keywords != previous.Keywords) changed.Add("$keywords");
        if (previous is not null && revision.ContentTypeId != previous.ContentTypeId) changed.Add("$content_type_id");
        changed.Sort(StringComparer.Ordinal);
        var number = (await db.ItemHistories.Where(x => x.ItemId == item.Id).MaxAsync(x => (long?)x.Number, ct) ?? 0) + 1;
        db.ItemHistories.Add(new ItemHistoriesRow
        {
            Id = NewId(),
            CreatedAt = Now,
            ItemId = item.Id,
            RevisionId = revision.Id,
            Number = number,
            ChangedFields = Encode(changed)
        });
        var entries = await db.ItemHistories.Where(x => x.ItemId == item.Id).OrderByDescending(x => x.Number).Skip((int)collection.MaxVersions - 1).ToListAsync(ct);
        db.ItemHistories.RemoveRange(entries);
    }

    private async Task SignalAsync<T>(string actor, ResourcesRow resource, string type, T data, bool notify, CancellationToken ct)
    {
        db.Set<DomainEventsRow>().Add(new DomainEventsRow
        {
            Id = NewId(),
            CreatedAt = Now,
            Type = type,
            WorkspaceId = resource.WorkspaceId,
            CollectionId = resource.ContainerId,
            ResourceId = resource.Id,
            Actor = actor,
            Data = Encode(data),
            Depth = 0
        });
        if (!notify) return;
        await db.LockLivePositionAsync(ct);
        var position = await db.Set<LiveChangePositionRow>().SingleAsync(x => x.Id == 1, ct);
        position.Position++;
        db.Set<LiveChangesRow>().Add(new LiveChangesRow { Id = position.Position, CreatedAt = Now, Kind = "item", EntityId = resource.Id, Action = type.EndsWith(".created", StringComparison.Ordinal) ? "created" : "updated" });
    }

    private Task AuditAsync<T>(string actor, ResourcesRow resource, string action, T details, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        db.Set<AuditEventsRow>().Add(new AuditEventsRow
        {
            Id = NewId(),
            CreatedAt = Now,
            WorkspaceId = resource.WorkspaceId,
            ResourceId = resource.Id,
            Subject = actor,
            Action = action,
            EntityType = resource.Kind,
            EntityId = resource.Id,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? NewId(),
            Details = Encode(details)
        });
        return Task.CompletedTask;
    }
}
