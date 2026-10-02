using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Data;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Term curation must preserve the meaning and constraints of existing graph predicates.</summary>
internal sealed class RelationshipTermMergeValidator(ListsDbContext db) : ITermMergeValidator
{
    public async Task<string?> ValidateAsync(Guid sourceId, Guid targetId, CancellationToken ct)
    {
        var source = await db.RelationshipTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == sourceId, ct);
        var target = await db.RelationshipTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId, ct);
        var sourceEdges = await db.Relations.AsNoTracking().Where(r => r.TypeId == sourceId).ToListAsync(ct);
        foreach (var edge in sourceEdges)
        {
            var duplicate = await db.Relations.AsNoTracking().FirstOrDefaultAsync(r => r.TypeId == targetId && r.FirstItemId == edge.FirstItemId && r.SecondItemId == edge.SecondItemId && r.Directed == edge.Directed, ct);
            if (duplicate is null) continue;
            try
            {
                if (RelationshipAttributes.Merge(RelationshipAttributes.Parse(duplicate.Attributes), RelationshipAttributes.Parse(edge.Attributes)) is null)
                    return "Matching relationships have conflicting attributes. Resolve the conflicts before merging predicates.";
            }
            catch (ArgumentException ex) { return ex.Message; }
        }
        if (source is null && target is null) return null;
        if (source is not null && target is not null && (source.Directed != target.Directed
            || source.MaxIncoming != target.MaxIncoming || source.MaxOutgoing != target.MaxOutgoing))
            return "Relationship types with different direction or limits cannot be merged.";
        if ((source?.MaxIncoming is not null || source?.MaxOutgoing is not null || target?.MaxIncoming is not null || target?.MaxOutgoing is not null)
            && await db.Relations.AnyAsync(r => r.TypeId == sourceId || r.TypeId == targetId, ct))
            return "Remove or retype the edges before merging predicates with endpoint limits. This preserves their cardinality.";
        var directed = target?.Directed ?? source?.Directed ?? false;
        if (await db.Relations.AnyAsync(r => (r.TypeId == sourceId || r.TypeId == targetId) && r.Directed != directed, ct))
            return "Existing relationships have a different direction from the merged predicate.";
        return null;
    }
}
