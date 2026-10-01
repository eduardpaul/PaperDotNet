using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// Rewrites stored managed metadata and keyword values after a term merge (source id → target id, without duplicates).
/// Each item is updated through the item store on behalf of the organization, so versions, events and the delta follow
/// as for any change. Idempotent: a second run finds nothing to change. Items in the recycle bin keep the old id (merged
/// terms still resolve to their target).
/// </summary>
public static class TermMergedSubscriber
{
    private const int BatchSize = 200;

    public static async Task Handle(TermMerged e, ListsDbContext database, ITermStore terms, IListItemStore items, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = e.TenantId;
        var ct = cancellationToken;
        var set = await terms.GetTermSetAsync(tenant, e.TermSetId, ct);
        if (set is null)
        {
            return;
        }

        // The content types with fields that store terms of this set.
        var contentTypes = await db.ContentTypes.AsNoTracking().Where(c => c.TenantId == tenant).ToListAsync(ct);
        var fieldsByContentType = contentTypes
            .Select(c => (c.Id, Fields: ListsJsonText.Fields(c.Fields).Where(f => Uses(f, set)).Select(f => f.Name).ToList()))
            .Where(c => c.Fields.Count > 0)
            .ToList();
        if (fieldsByContentType.Count == 0)
        {
            return;
        }

        var source = FieldFormats.Identifier(e.SourceTermId);
        var target = FieldFormats.Identifier(e.TargetTermId);
        var store = items.AsSystem(new ChangeActor(tenant, e.UserId, e.Depth + 1));
        var workspaces = new Dictionary<Guid, Guid?>();
        foreach (var (contentTypeId, fields) in fieldsByContentType)
        {
            var type = contentTypeId;
            var after = Guid.Empty;
            var take = BatchSize;
            while (true)
            {
                var from = after;
                var batch = await db.Items.AsNoTracking()
                    .Where(i => i.TenantId == tenant && i.ContentTypeId == type && i.DeletedAt == null && i.Fields.Contains(source) && i.Id.CompareTo(from) > 0)
                    .OrderBy(i => i.Id).Take(take).ToListAsync(ct);
                foreach (var item in batch)
                {
                    var changes = Replace(item.Fields, fields, source, target);
                    if (changes.Count == 0)
                    {
                        continue;
                    }

                    if (!workspaces.TryGetValue(item.ListId, out var workspaceId))
                    {
                        var list = item.ListId;
                        workspaces[list] = workspaceId = await db.Lists.AsNoTracking().Where(l => l.TenantId == tenant && l.Id == list).Select(l => (Guid?)l.WorkspaceId).FirstOrDefaultAsync(ct);
                    }

                    if (workspaceId is { } workspace)
                    {
                        await store.UpdateAsync(workspace, item.ListId, item.Id, changes, null, ct);
                    }
                }

                if (batch.Count < take)
                {
                    break;
                }

                after = batch[^1].Id;
            }
        }
    }

    private static bool Uses(FieldDefinition field, TermSetInfo set) =>
        (field.Type == ManagedMetadataFieldType.TypeName && field.TermSetId == set.Id)
        || (field.Type == KeywordsFieldType.TypeName && set.IsKeywords);

    /// <summary>The new values of the <paramref name="fields"/> that hold <paramref name="source"/>.</summary>
    internal static JsonObject Replace(string json, IReadOnlyList<string> fields, string source, string target)
    {
        var values = JsonNode.Parse(json)?.AsObject() ?? [];
        var changes = new JsonObject();
        foreach (var name in fields)
        {
            switch (values[name])
            {
                case JsonArray array when array.Any(v => v?.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == source):
                    var ids = array.Select(v => v?.GetValue<string>()).Select(v => v == source ? target : v).Distinct().ToList();
                    changes[name] = new JsonArray([.. ids.Select(v => (JsonNode?)JsonValue.Create(v))]);
                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == source:
                    changes[name] = target;
                    break;
            }
        }

        return changes;
    }
}
