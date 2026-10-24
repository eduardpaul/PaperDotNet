using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Content;

public sealed partial class ContentStore
{
    private async Task ProjectAsync(ResourcesRow item, ItemRevisionsRow revision, string kind, CancellationToken ct)
    {
        var surface = await db.ItemSurfaces.SingleOrDefaultAsync(x => x.ItemId == item.Id && x.Surface == kind, ct);
        if (surface is not null)
        {
            await CountAsync(surface, -1, ct);
            db.Set<FieldValuesRow>().RemoveRange(await db.Set<FieldValuesRow>().Where(x => x.SurfaceId == surface.Id).ToListAsync(ct));
            db.Set<ItemSurfaceKeywordsRow>().RemoveRange(await db.Set<ItemSurfaceKeywordsRow>().Where(x => x.SurfaceId == surface.Id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
        else
        {
            surface = new ItemSurfacesRow { Id = NewId(), CreatedAt = Now, ItemId = item.Id, Surface = kind };
            db.ItemSurfaces.Add(surface);
        }
        surface.ContainerId = item.ContainerId!;
        surface.WorkspaceId = item.WorkspaceId;
        surface.RevisionId = revision.Id;
        surface.Name = revision.Name;
        surface.FoldedName = revision.Name.ToLowerInvariant();
        surface.Keywords = revision.Keywords;
        surface.Payload = revision.Payload;
        surface.ItemCreatedAt = item.CreatedAt;
        surface.ItemCreatedBy = item.CreatedBy;
        surface.ModifiedAt = revision.CreatedAt;
        surface.ModifiedBy = revision.CreatedBy;
        var schema = await db.SchemaRevisions.SingleAsync(x => x.Id == revision.SchemaRevisionId, ct);
        var frozen = Decode<EffectiveSchema>(schema.Definition);
        var collection = await db.Resources.SingleAsync(x => x.Id == item.ContainerId, ct);
        var currentSchema = await db.SchemaRevisions.SingleAsync(x => x.Id == collection.SchemaHeadId, ct);
        var current = Decode<EffectiveSchema>(currentSchema.Definition).Fields.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var type = frozen.ContentTypes.Single(x => x.Id == revision.ContentTypeId);
        var fields = frozen.Fields.Where(x => type.FieldKeys.Contains(x.Id, StringComparer.Ordinal)
            && current.TryGetValue(x.Id, out var now) && now.Indexed && SameIdentity(x, now));
        var payload = Decode<Dictionary<string, JsonElement>>(revision.Payload);
        foreach (var field in fields)
        {
            if (!payload.TryGetValue(field.Id, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            var values = field.Options?.Multiple == true ? value.EnumerateArray().ToArray() : [value];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
            {
                var row = IndexValue(field, values[ordinal]);
                row.Id = NewId(); row.CreatedAt = Now; row.SurfaceId = surface.Id;
                row.ContainerId = surface.ContainerId; row.ItemId = item.Id; row.Surface = kind; row.Ordinal = ordinal;
                db.Set<FieldValuesRow>().Add(row);
            }
        }
        foreach (var keyword in Decode<string[]>(revision.Keywords))
            db.Set<ItemSurfaceKeywordsRow>().Add(new ItemSurfaceKeywordsRow { SurfaceId = surface.Id, Keyword = keyword });
        await db.SaveChangesAsync(ct);
        await CountAsync(surface, 1, ct);
        await SyncBusinessKeysAsync(item, ct);
        await db.SaveChangesAsync(ct);
    }

    private static FieldValuesRow IndexValue(SchemaField field, JsonElement value)
    {
        var row = new FieldValuesRow
        {
            FieldKey = field.Id,
            FieldType = field.Type,
            Scale = field.Scale,
            AppliesParents = field.Options?.ApplyParents == true
        };
        switch (field.Type)
        {
            case "integer": row.ValueInteger = value.GetInt64(); break;
            case "decimal": row.ValueInteger = ScaledDecimal(value, field.Scale).Scaled; break;
            case "number": case "currency": row.ValueText = NumberKey(value); break;
            case "boolean": row.ValueBoolean = value.GetBoolean(); break;
            case "datetime": row.ValueText = NormalizeInstant(value.GetString()!); break;
            default: row.ValueText = value.GetString(); break;
        }
        return row;
    }

    private async Task RemoveSurfaceAsync(string itemId, string kind, CancellationToken ct)
    {
        var surface = await db.ItemSurfaces.SingleOrDefaultAsync(x => x.ItemId == itemId && x.Surface == kind, ct);
        if (surface is null) return;
        await CountAsync(surface, -1, ct);
        // Explicit dependent deletion avoids tracked cascade cycles through immutable revision records.
        db.Set<FieldValuesRow>().RemoveRange(await db.Set<FieldValuesRow>().Where(x => x.SurfaceId == surface.Id).ToListAsync(ct));
        db.Set<ItemSurfaceKeywordsRow>().RemoveRange(await db.Set<ItemSurfaceKeywordsRow>().Where(x => x.SurfaceId == surface.Id).ToListAsync(ct));
        db.ItemSurfaces.Remove(surface);
        await db.SaveChangesAsync(ct);
    }

    private async Task CountAsync(ItemSurfacesRow surface, int delta, CancellationToken ct)
    {
        await AddCountAsync(surface, "", "", delta, ct);
        var choices = await db.Set<FieldValuesRow>().Where(x => x.SurfaceId == surface.Id && x.FieldType == "choice" && x.Ordinal == 0 && x.ValueText != null).ToListAsync(ct);
        foreach (var value in choices) await AddCountAsync(surface, value.FieldKey, value.ValueText!, delta, ct);
    }

    private async Task AddCountAsync(ItemSurfacesRow surface, string key, string value, int delta, CancellationToken ct)
    {
        var counter = await db.Set<GroupCountsRow>().FindAsync([surface.ContainerId, surface.Surface, key, value], ct);
        if (counter is null)
        {
            if (delta < 0) throw new InvalidOperationException("Surface counters are inconsistent.");
            db.Set<GroupCountsRow>().Add(new GroupCountsRow
            {
                ContainerId = surface.ContainerId,
                Surface = surface.Surface,
                FieldKey = key,
                Value = value,
                Count = delta
            });
        }
        else
        {
            counter.Count += delta;
            if (counter.Count == 0) db.Set<GroupCountsRow>().Remove(counter);
        }
    }

    private async Task ReplaceResourceKeywordsAsync(ResourcesRow item, IReadOnlyList<string> keywords, CancellationToken ct)
    {
        var existing = await db.Set<ResourceKeywordsRow>().Where(x => x.ResourceId == item.Id).ToListAsync(ct);
        db.Set<ResourceKeywordsRow>().RemoveRange(existing.Where(x => !keywords.Contains(x.Keyword, StringComparer.Ordinal)));
        foreach (var keyword in keywords.Where(x => existing.All(k => k.Keyword != x)))
            db.Set<ResourceKeywordsRow>().Add(new ResourceKeywordsRow { ResourceId = item.Id, Keyword = keyword });
    }

    private async Task SyncBusinessKeysAsync(ResourcesRow item, CancellationToken ct)
    {
        var collection = await db.Resources.SingleAsync(x => x.Id == item.ContainerId, ct);
        var schema = await db.SchemaRevisions.SingleAsync(x => x.Id == collection.SchemaHeadId, ct);
        var unique = Decode<EffectiveSchema>(schema.Definition).Fields.Where(x => x.Options?.Unique == true).ToArray();
        var existing = await db.Set<BusinessKeysRow>().Where(x => x.ItemId == item.Id).ToListAsync(ct);
        var desired = new HashSet<(string Field, string Value)>();
        var surfaces = await db.ItemSurfaces.Where(x => x.ItemId == item.Id).ToListAsync(ct);
        foreach (var surface in surfaces)
        {
            var values = Decode<Dictionary<string, JsonElement>>(surface.Payload);
            foreach (var field in unique)
            {
                if (!values.TryGetValue(field.Id, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                var indexed = IndexValue(field, value);
                var canonical = indexed.ValueText is not null ? Encode(indexed.ValueText)
                    : indexed.ValueInteger is not null ? Encode(indexed.ValueInteger.Value) : Encode(indexed.ValueBoolean!.Value);
                desired.Add((field.Id, canonical));
            }
        }
        db.Set<BusinessKeysRow>().RemoveRange(existing.Where(x => !desired.Contains((x.FieldKey, x.Value))));
        foreach (var key in desired.Where(x => existing.All(e => e.FieldKey != x.Field || e.Value != x.Value)))
            db.Set<BusinessKeysRow>().Add(new BusinessKeysRow { Id = NewId(), CreatedAt = Now, ContainerId = item.ContainerId!, ItemId = item.Id, FieldKey = key.Field, Value = key.Value });
    }

    private static bool SameIdentity(SchemaField left, SchemaField right) => left.Id == right.Id && left.DefinitionId == right.DefinitionId
        && left.Type == right.Type && left.Scale == right.Scale && left.Options?.Multiple == right.Options?.Multiple
        && left.Options?.ApplyParents == right.Options?.ApplyParents
        && left.Options?.TermSetId == right.Options?.TermSetId && left.Options?.LookupContainerId == right.Options?.LookupContainerId;

    private static string NumberKey(JsonElement value)
    {
        var number = value.GetDecimal();
        var raw = Math.Abs(number).ToString("0.############################", CultureInfo.InvariantCulture).Split('.');
        var digits = raw[0].PadLeft(29, '0') + (raw.Length == 2 ? raw[1] : "").PadRight(28, '0');
        return number < 0 ? "0" + string.Concat(digits.Select(c => (char)('9' - c + '0'))) : "1" + digits;
    }
}
