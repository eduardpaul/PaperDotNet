using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Taxonomy.Data;

namespace PaperDotNet.Taxonomy.Features;

/// <summary>The term store for other modules. Query rules as in every module (ADR-0039): locals, one expression, the tenant named.</summary>
internal sealed class TermStore(TaxonomyDbContext db) : ITermStore
{
    /// <summary>Upper bound for following merge chains (they are flattened on merge, so 1 is normal).</summary>
    private const int MaxMergeHops = 8;

    public async Task<TermSetInfo?> GetTermSetAsync(Guid tenantId, Guid termSetId, CancellationToken cancellationToken) =>
        await FindSetAsync(db, tenantId, termSetId, cancellationToken) is { } s ? new TermSetInfo(s.Id, s.GroupId, s.Name, s.IsOpen, s.IsKeywords) : null;

    public async Task<string?> GetTermSetPathAsync(Guid tenantId, Guid termSetId, CancellationToken cancellationToken)
    {
        if (await FindSetAsync(db, tenantId, termSetId, cancellationToken) is not { } set)
        {
            return null;
        }

        return await FindGroupAsync(db, tenantId, set.GroupId, cancellationToken) is { } group ? $"{group.Name}/{set.Name}" : null;
    }

    public async Task<Guid?> FindTermSetAsync(Guid tenantId, string groupName, string setName, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var group = groupName;
        var name = setName;
        var ct = cancellationToken;
        var groupId = await context.Groups.AsNoTracking().Where(g => g.TenantId == tenant && g.Name == group).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct);
        if (groupId is not { } id)
        {
            return null;
        }

        return await context.TermSets.AsNoTracking().Where(s => s.TenantId == tenant && s.GroupId == id && s.Name == name).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<TermSetInfo> GetKeywordsSetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var set = await EnsureKeywordsSetAsync(db, tenantId, cancellationToken);
        return new TermSetInfo(set.Id, set.GroupId, set.Name, set.IsOpen, set.IsKeywords);
    }

    public async Task<Guid?> ResolveAsync(Guid tenantId, Guid termSetId, string value, bool allowCreate, CancellationToken cancellationToken)
    {
        var set = await FindSetAsync(db, tenantId, termSetId, cancellationToken);
        if (set is null)
        {
            return null;
        }

        // Keywords fields also accept terms promoted from the keywords set (TAX-05).
        var keywords = set.IsKeywords;
        if (Guid.TryParse(value, out var id))
        {
            for (var hop = 0; hop < MaxMergeHops; hop++)
            {
                var term = await FindTermAsync(db, tenantId, id, cancellationToken);
                if (term is null || (term.TermSetId != termSetId && !(keywords && term.AvailableAsKeyword)))
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

        var matches = await FindByLabelAsync(db, tenantId, termSetId, label, cancellationToken, includePromoted: keywords);
        if (matches.Count == 1)
        {
            return matches[0].IsDeprecated ? null : matches[0].Id;
        }

        if (matches.Count > 1 || !allowCreate || !set.IsOpen)
        {
            return null;
        }

        var created = TermRules.NewTerm(tenantId, set.Id, null, null, label);
        db.Terms.Add(created);
        await db.SaveChangesAsync(cancellationToken);
        return created.Id;
    }

    public async Task<IReadOnlyList<TermInfo>> ListTermsAsync(Guid tenantId, Guid termSetId, CancellationToken cancellationToken)
    {
        if (await FindSetAsync(db, tenantId, termSetId, cancellationToken) is not { } set)
        {
            return [];
        }

        var context = db;
        var tenant = tenantId;
        var setId = termSetId;
        var ct = cancellationToken;
        var terms = await context.Terms.AsNoTracking().Where(t => t.TenantId == tenant && t.TermSetId == setId && !t.IsDeprecated).OrderBy(t => t.Name).ToListAsync(ct);
        return [.. terms.Select(t => new TermInfo(t.Id, t.TermSetId, t.Name, set.IsKeywords || t.AvailableAsKeyword, t.IsDeprecated))];
    }

    public async Task<IReadOnlyList<TermInfo>> GetTermsAsync(Guid tenantId, IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken)
    {
        var result = new List<TermInfo>();
        var keywordSets = new Dictionary<Guid, bool>();
        foreach (var id in termIds.Distinct())
        {
            if (await FindTermAsync(db, tenantId, id, cancellationToken) is not { } term)
            {
                continue;
            }

            if (!keywordSets.TryGetValue(term.TermSetId, out var isKeywords))
            {
                keywordSets[term.TermSetId] = isKeywords = (await FindSetAsync(db, tenantId, term.TermSetId, cancellationToken))?.IsKeywords == true;
            }

            result.Add(new TermInfo(term.Id, term.TermSetId, term.Name, isKeywords || term.AvailableAsKeyword, term.IsDeprecated));
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetTermPathsAsync(Guid tenantId, IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var id in termIds.Distinct())
        {
            if (await FindTermAsync(db, tenantId, id, cancellationToken) is not { } term
                || await GetTermSetPathAsync(tenantId, term.TermSetId, cancellationToken) is not { } setPath)
            {
                continue;
            }

            var names = new List<string>();
            foreach (var ancestor in term.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse))
            {
                names.Add(ancestor == term.Id ? term.Name : (await FindTermAsync(db, tenantId, ancestor, cancellationToken))?.Name ?? "?");
            }

            result[term.Id] = setPath + "/" + string.Join('/', names);
        }

        return result;
    }

    public async Task<Guid?> FindTermByPathAsync(Guid tenantId, string path, CancellationToken cancellationToken)
    {
        var parts = path.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || await FindTermSetAsync(tenantId, parts[0], parts[1], cancellationToken) is not { } setId)
        {
            return null;
        }

        Guid? parent = null;
        foreach (var name in parts.Skip(2))
        {
            parent = await FindChildAsync(db, tenantId, setId, parent, TermRules.Normalize(name), cancellationToken);
            if (parent is null)
            {
                return null;
            }
        }

        return parent;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetDescendantsAsync(Guid tenantId, IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var id in termIds.Distinct())
        {
            if (await FindTermAsync(db, tenantId, id, cancellationToken) is { } root)
            {
                result[root.Id] = await SubtreeAsync(db, tenantId, root.Path, cancellationToken);
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetLabelsAsync(Guid tenantId, IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var id in termIds.Distinct())
        {
            if (await FindTermAsync(db, tenantId, id, cancellationToken) is { } t)
            {
                result[t.Id] = [t.Name, .. t.GetLabels().Select(l => l.Name), .. t.GetSynonyms()];
            }
        }

        return result;
    }

    // ---- Queries shared with the endpoints ----------------------------------------------------

    internal static Task<TermGroup?> FindGroupAsync(TaxonomyDbContext database, Guid tenantId, Guid groupId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = groupId;
        var ct = cancellationToken;
        return context.Groups.FirstOrDefaultAsync(g => g.TenantId == tenant && g.Id == id, ct);
    }

    internal static Task<TermSet?> FindSetAsync(TaxonomyDbContext database, Guid tenantId, Guid termSetId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = termSetId;
        var ct = cancellationToken;
        return context.TermSets.FirstOrDefaultAsync(s => s.TenantId == tenant && s.Id == id, ct);
    }

    internal static Task<Term?> FindTermAsync(TaxonomyDbContext database, Guid tenantId, Guid termId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = termId;
        var ct = cancellationToken;
        return context.Terms.FirstOrDefaultAsync(t => t.TenantId == tenant && t.Id == id, ct);
    }

    /// <summary>The active child of <paramref name="parentId"/> (null: a root term) with that normalized name.</summary>
    internal static Task<Guid?> FindChildAsync(TaxonomyDbContext database, Guid tenantId, Guid termSetId, Guid? parentId, string normalizedName, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var setId = termSetId;
        var name = normalizedName;
        var ct = cancellationToken;
        if (parentId is { } parent)
        {
            return context.Terms.AsNoTracking().Where(t => t.TenantId == tenant && t.TermSetId == setId && t.ParentId == parent && t.NormalizedName == name && t.MergedIntoId == null)
                .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        }

        return context.Terms.AsNoTracking().Where(t => t.TenantId == tenant && t.TermSetId == setId && t.ParentId == null && t.NormalizedName == name && t.MergedIntoId == null)
            .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>The ids of the active terms at or below <paramref name="path"/>.</summary>
    internal static async Task<IReadOnlyList<Guid>> SubtreeAsync(TaxonomyDbContext database, Guid tenantId, string path, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var prefix = path;
        var ct = cancellationToken;
        return await context.Terms.AsNoTracking().Where(t => t.TenantId == tenant && t.Path.StartsWith(prefix) && t.MergedIntoId == null).Select(t => t.Id).ToListAsync(ct);
    }

    /// <summary>Active (not merged) terms whose name, label or synonym equals <paramref name="label"/> (case-insensitive).</summary>
    internal static async Task<List<Term>> FindByLabelAsync(TaxonomyDbContext database, Guid tenantId, Guid termSetId, string label, CancellationToken cancellationToken, bool includePromoted = false)
    {
        var context = database;
        var tenant = tenantId;
        var setId = termSetId;
        var normalized = TermRules.Normalize(label);
        var promoted = includePromoted;
        var ct = cancellationToken;
        // One static query per option (precompiled queries, ADR-0039).
        var candidates = promoted
            ? await context.Terms.AsNoTracking()
                .Where(t => t.TenantId == tenant && (t.TermSetId == setId || t.AvailableAsKeyword) && t.MergedIntoId == null && t.SearchText.Contains(normalized))
                .ToListAsync(ct)
            : await context.Terms.AsNoTracking()
                .Where(t => t.TenantId == tenant && t.TermSetId == setId && t.MergedIntoId == null && t.SearchText.Contains(normalized))
                .ToListAsync(ct);
        return [.. candidates.Where(t => t.SearchText.Split('\n').Contains(normalized, StringComparer.Ordinal))];
    }

    /// <summary>The System group and its open Keywords set, created when missing.</summary>
    internal static async Task<TermSet> EnsureKeywordsSetAsync(TaxonomyDbContext database, Guid tenantId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var ct = cancellationToken;
        for (var attempt = 0; ; attempt++)
        {
            var existing = await context.TermSets.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenant && s.IsKeywords, ct);
            if (existing is not null)
            {
                return existing;
            }

            var group = await context.Groups.FirstOrDefaultAsync(g => g.TenantId == tenant && g.IsSystem, ct);
            if (group is null)
            {
                group = new TermGroup { Id = Ids.New(), TenantId = tenant, Name = TermGroup.SystemName, Description = "System term sets.", IsSystem = true };
                context.Groups.Add(group);
            }

            var set = new TermSet
            {
                Id = Ids.New(),
                TenantId = tenant,
                GroupId = group.Id,
                Name = TermSet.KeywordsName,
                Description = "Free tags (folksonomy). Anyone can add keywords.",
                IsOpen = true,
                IsKeywords = true,
            };
            context.TermSets.Add(set);
            try
            {
                await context.SaveChangesAsync(ct);
                return set;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Created concurrently: the unique indexes reject the duplicate; read it back.
                context.ChangeTracker.Clear();
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

    public static Term NewTerm(Guid tenantId, Guid termSetId, Guid? parentId, string? parentPath, string name)
    {
        var id = Ids.New();
        var term = new Term
        {
            Id = id,
            TenantId = tenantId,
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
