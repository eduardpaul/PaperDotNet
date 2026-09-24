using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Taxonomy.Data;

namespace PaperDotNet.Taxonomy.Features;

internal sealed class TermStore(TaxonomyDbContext db) : ITermStore
{
    /// <summary>Upper bound for following merge chains (they are flattened on merge, so 1 is normal).</summary>
    private const int MaxMergeHops = 8;

    public async Task<TermSetInfo?> GetTermSetAsync(Guid termSetId, CancellationToken cancellationToken) =>
        await db.TermSets.AsNoTracking()
            .Where(s => s.Id == termSetId)
            .Select(s => new TermSetInfo(s.Id, s.GroupId, s.Name, s.IsOpen, s.IsKeywords))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<TermSetInfo> GetKeywordsSetAsync(CancellationToken cancellationToken)
    {
        var set = await EnsureKeywordsSetAsync(db, cancellationToken);
        return new TermSetInfo(set.Id, set.GroupId, set.Name, set.IsOpen, set.IsKeywords);
    }

    public async Task<Guid?> ResolveAsync(Guid termSetId, string value, bool allowCreate, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(value, out var id))
        {
            for (var hop = 0; hop < MaxMergeHops; hop++)
            {
                var term = await db.Terms.AsNoTracking()
                    .Where(t => t.Id == id && t.TermSetId == termSetId)
                    .Select(t => new { t.MergedIntoId, t.IsDeprecated })
                    .FirstOrDefaultAsync(cancellationToken);
                if (term is null)
                {
                    return null;
                }

                if (term.MergedIntoId is not { } target)
                {
                    return term.IsDeprecated ? null : id;
                }

                id = target;
            }

            return null;
        }

        var label = value.Trim();
        if (label.Length is 0 or > TermRules.NameMaxLength)
        {
            return null;
        }

        var matches = await FindByLabelAsync(db, termSetId, label, cancellationToken);
        if (matches.Count == 1)
        {
            return matches[0].IsDeprecated ? null : matches[0].Id;
        }

        if (matches.Count > 1 || !allowCreate)
        {
            return null;
        }

        var set = await db.TermSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == termSetId, cancellationToken);
        if (set is not { IsOpen: true })
        {
            return null;
        }

        var created = TermRules.NewTerm(set.Id, null, null, label);
        db.Terms.Add(created);
        await db.SaveChangesAsync(cancellationToken);
        return created.Id;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetDescendantsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        if (termIds.Count == 0)
        {
            return result;
        }

        var roots = await db.Terms.AsNoTracking()
            .Where(t => termIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Path })
            .ToListAsync(cancellationToken);
        foreach (var root in roots)
        {
            var path = root.Path;
            result[root.Id] = await db.Terms.AsNoTracking()
                .Where(t => t.Path.StartsWith(path) && t.MergedIntoId == null)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>Active (not merged) terms whose name, label or synonym equals <paramref name="label"/> (case-insensitive).</summary>
    internal static async Task<List<Term>> FindByLabelAsync(TaxonomyDbContext db, Guid termSetId, string label, CancellationToken ct)
    {
        var normalized = TermRules.Normalize(label);
        var candidates = await db.Terms.AsNoTracking()
            .Where(t => t.TermSetId == termSetId && t.MergedIntoId == null && t.SearchText.Contains(normalized))
            .ToListAsync(ct);
        return candidates
            .Where(t => t.SearchText.Split('\n').Contains(normalized, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>The System group and its open Keywords set, created when missing.</summary>
    internal static async Task<TermSet> EnsureKeywordsSetAsync(TaxonomyDbContext db, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.TermSets.AsNoTracking().FirstOrDefaultAsync(s => s.IsKeywords, ct);
            if (existing is not null)
            {
                return existing;
            }

            var group = await db.Groups.FirstOrDefaultAsync(g => g.IsSystem, ct);
            if (group is null)
            {
                group = new TermGroup { Id = Ids.New(), Name = TermGroup.SystemName, Description = "System term sets.", IsSystem = true };
                db.Groups.Add(group);
            }

            var set = new TermSet
            {
                Id = Ids.New(),
                GroupId = group.Id,
                Name = TermSet.KeywordsName,
                Description = "Free tags (folksonomy). Anyone can add keywords.",
                IsOpen = true,
                IsKeywords = true,
            };
            db.TermSets.Add(set);
            try
            {
                await db.SaveChangesAsync(ct);
                return set;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Created concurrently: the unique indexes reject the duplicate; read it back.
                db.ChangeTracker.Clear();
            }
        }
    }
}

/// <summary>Shared rules for term names and paths.</summary>
internal static class TermRules
{
    public const int NameMaxLength = 255;

    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public static string PathOf(string? parentPath, Guid id) => $"{parentPath ?? "/"}{id:N}/";

    public static Term NewTerm(Guid termSetId, Guid? parentId, string? parentPath, string name)
    {
        var id = Ids.New();
        var term = new Term
        {
            Id = id,
            TermSetId = termSetId,
            ParentId = parentId,
            Name = name.Trim(),
            NormalizedName = Normalize(name),
            Path = PathOf(parentPath, id),
        };
        term.RefreshSearchText();
        return term;
    }
}

/// <summary>Creates the System group and the Keywords term set for a new tenant.</summary>
internal sealed class TaxonomyTenantInitializer(TaxonomyDbContext db) : ITenantInitializer
{
    public async Task InitializeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await TermStore.EnsureKeywordsSetAsync(db, cancellationToken);
}
