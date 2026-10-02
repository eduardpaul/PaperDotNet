using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// Rewrites stored managed metadata and keyword values after a term merge
/// (source id → target id, without duplicates) and publishes <see cref="ItemUpdated"/>
/// for each changed item. Idempotent: a second run finds nothing to change.
/// </summary>
internal sealed class TermMergedSubscriber(ListsDbContext db, ITermStore terms, IOutbox outbox) : IEventSubscriber<TermMerged>
{
    private const int BatchSize = 200;

    public async Task HandleAsync(TermMerged integrationEvent, CancellationToken cancellationToken)
    {
        var set = await terms.GetTermSetAsync(integrationEvent.TermSetId, cancellationToken);
        if (set is null)
        {
            return;
        }

        await MergeRelationshipsAsync(integrationEvent, cancellationToken);

        var fieldsByContentType = (await db.ContentTypes.AsNoTracking().ToListAsync(cancellationToken))
            .Select(c => (c.Id, Fields: c.Fields.Where(f => Uses(f, set)).ToList()))
            .Where(c => c.Fields.Count > 0)
            .ToDictionary(c => c.Id, c => c.Fields);
        if (fieldsByContentType.Count == 0)
        {
            return;
        }

        var source = FieldFormats.Identifier(integrationEvent.SourceTermId);
        var target = FieldFormats.Identifier(integrationEvent.TargetTermId);
        foreach (var field in fieldsByContentType.Values.SelectMany(f => f).DistinctBy(f => (f.Name, f.AllowMultiple)))
        {
            var fragment = new JsonObject { [field.Name] = field.AllowMultiple ? new JsonArray(source) : source }.ToJsonString();
            var contentTypeIds = fieldsByContentType.Where(c => c.Value.Any(f => f.Name == field.Name && f.AllowMultiple == field.AllowMultiple)).Select(c => c.Key).ToList();
            while (true)
            {
                var items = await db.Items
                    .Where(i => contentTypeIds.Contains(i.ContentTypeId) && JsonFunctions.Contains(i.Fields, fragment))
                    .OrderBy(i => i.Id)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);
                if (items.Count == 0)
                {
                    break;
                }

                var workspaces = await db.Lists.AsNoTracking()
                    .Where(l => items.Select(i => i.ListId).Contains(l.Id))
                    .ToDictionaryAsync(l => l.Id, l => l.WorkspaceId, cancellationToken);
                var events = new List<IntegrationEvent>();
                foreach (var item in items)
                {
                    item.Fields = Replace(item.Fields, field.Name, source, target);
                    events.Add(new ItemUpdated
                    {
                        TenantId = integrationEvent.TenantId,
                        TenantIdentifier = integrationEvent.TenantIdentifier,
                        UserId = integrationEvent.UserId,
                        WorkspaceId = workspaces.GetValueOrDefault(item.ListId),
                        ListId = item.ListId,
                        ItemId = item.Id,
                        ContentTypeId = item.ContentTypeId,
                        IsFolder = item.IsFolder,
                        ChangedFields = [field.Name],
                    });
                }

                await outbox.SaveChangesAsync(db, events, cancellationToken: cancellationToken);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task MergeRelationshipsAsync(TermMerged e, CancellationToken ct)
    {
        var definition = await db.RelationshipTypes.FirstOrDefaultAsync(t => t.Id == e.SourceTermId, ct);
        if (definition is not null)
        {
            if (!await db.RelationshipTypes.AnyAsync(t => t.Id == e.TargetTermId, ct))
                db.RelationshipTypes.Add(new ItemRelationshipType
                {
                    Id = e.TargetTermId,
                    Directed = definition.Directed,
                    InverseLabel = definition.InverseLabel,
                    MaxIncoming = definition.MaxIncoming,
                    MaxOutgoing = definition.MaxOutgoing
                });
            db.RelationshipTypes.Remove(definition);
            await db.SaveChangesAsync(ct);
        }
        while (true)
        {
            var edges = await db.Relations.Where(r => r.TypeId == e.SourceTermId).OrderBy(r => r.Id).Take(BatchSize).ToListAsync(ct);
            if (edges.Count == 0) break;
            foreach (var edge in edges)
            {
                if (await db.Relations.AnyAsync(r => r.TypeId == e.TargetTermId && r.FirstItemId == edge.FirstItemId
                    && r.SecondItemId == edge.SecondItemId && r.Directed == edge.Directed, ct)) db.Relations.Remove(edge);
                else edge.TypeId = e.TargetTermId;
            }
            var ids = edges.SelectMany(r => new[] { r.FirstItemId, r.SecondItemId }).Distinct().ToArray();
            var items = await db.Items.Where(i => EF.Parameter(ids).Contains(i.Id)).ToListAsync(ct);
            var listIds = items.Select(i => i.ListId).Distinct().ToArray();
            var workspaces = await db.Lists.Where(l => EF.Parameter(listIds).Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.WorkspaceId, ct);
            foreach (var item in items) db.Entry(item).Property(i => i.UpdatedAt).IsModified = true;
            await outbox.SaveChangesAsync(db, items.Select(i => (IntegrationEvent)new ItemUpdated
            {
                TenantId = e.TenantId,
                TenantIdentifier = e.TenantIdentifier,
                UserId = e.UserId,
                WorkspaceId = workspaces[i.ListId],
                ListId = i.ListId,
                ItemId = i.Id,
                ContentTypeId = i.ContentTypeId,
                ChangedFields = ["relatedItems"]
            }).ToList(), cancellationToken: ct);
            db.ChangeTracker.Clear();
        }
    }

    private static bool Uses(FieldDefinition field, TermSetInfo set) =>
        (field.Type == ManagedMetadataFieldType.TypeName && field.TermSetId == set.Id)
        || (field.Type == KeywordsFieldType.TypeName && set.IsKeywords);

    internal static string Replace(string fields, string name, string source, string target)
    {
        var values = JsonNode.Parse(fields)!.AsObject();
        switch (values[name])
        {
            case JsonArray array:
                var ids = array.Select(v => v?.GetValue<string>()).Select(v => v == source ? target : v).Distinct().ToList();
                values[name] = new JsonArray([.. ids.Select(v => (JsonNode?)JsonValue.Create(v))]);
                break;
            case JsonValue value when value.GetValue<string>() == source:
                values[name] = target;
                break;
        }

        return values.ToJsonString();
    }
}
