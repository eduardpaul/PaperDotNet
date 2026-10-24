using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Content;

public sealed partial class ContentStore
{
    public async Task<IReadOnlyList<ContentHistoryView>> HistoryAsync(string actor, string itemId, long? beforeNumber = null,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var item = await RequireAsync(actor, itemId, "read", cancellationToken);
        if (item.Kind != "item") throw new InvalidOperationException("Only items have retained history.");
        var query = db.ItemHistories.AsNoTracking().Where(x => x.ItemId == item.Id);
        if (beforeNumber is not null) query = query.Where(x => x.Number < beforeNumber);
        var histories = await query.OrderByDescending(x => x.Number).Take(limit).ToListAsync(cancellationToken);
        var result = new List<ContentHistoryView>(histories.Count);
        foreach (var history in histories)
        {
            var revision = await db.ItemRevisions.AsNoTracking().SingleAsync(x => x.Id == history.RevisionId, cancellationToken);
            var schema = await db.SchemaRevisions.AsNoTracking().SingleAsync(x => x.Id == revision.SchemaRevisionId, cancellationToken);
            result.Add(new ContentHistoryView(history.Number, Decode<string[]>(history.ChangedFields), revision, schema));
        }
        return result;
    }

    public Task<ResourcesRow> RestoreHistoryAsync(string actor, string itemId, long expectedVersion, long historyNumber,
        CancellationToken cancellationToken = default) => WriteAsync(itemId, async ct =>
        {
            var item = await RequireAsync(actor, itemId, "write", ct);
            CheckVersion(item, expectedVersion);
            if (item.Kind != "item") throw new InvalidOperationException("Only items have retained history.");
            var history = await db.ItemHistories.SingleOrDefaultAsync(x => x.ItemId == item.Id && x.Number == historyNumber, ct)
                ?? throw new KeyNotFoundException("Retained history entry does not exist.");
            var snapshot = await db.ItemRevisions.SingleAsync(x => x.Id == history.RevisionId, ct);
            var previous = await db.ItemRevisions.SingleAsync(x => x.Id == item.HeadRevisionId, ct);
            var collection = await db.Resources.SingleAsync(x => x.Id == item.ContainerId, ct);
            var schema = await db.SchemaRevisions.SingleAsync(x => x.Id == collection.SchemaHeadId, ct);
            var type = Decode<EffectiveSchema>(schema.Definition).ContentTypes.SingleOrDefault(x => x.Id == snapshot.ContentTypeId)
                ?? throw new InvalidOperationException("The current schema no longer accepts the historical content type.");
            var values = Decode<Dictionary<string, JsonElement>>(snapshot.Payload);
            foreach (var key in values.Keys.Where(x => !type.FieldKeys.Contains(x, StringComparer.Ordinal)).ToArray()) values.Remove(key);
            foreach (var key in type.FieldKeys) values.TryAdd(key, JsonSerializer.SerializeToElement<object?>(null));
            item.Version++;
            await ReviseAsync(actor, item, collection, new ContentInput(snapshot.Name, snapshot.ContentTypeId,
                JsonSerializer.SerializeToElement(values), Decode<string[]>(snapshot.Keywords)), previous, ct);
            await AuditAsync(actor, item, "item.restore_revision", new { restored_revision_number = historyNumber, revision_id = item.HeadRevisionId }, ct);
            return item;
        }, cancellationToken);
}
