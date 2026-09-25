using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Data;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>What to search for; see <c>GET /v1.0/search</c>.</summary>
internal sealed record SearchRequest(
    string? Q,
    SearchMode? Mode,
    Guid? WorkspaceId = null,
    Guid? ContainerId = null,
    Guid? ContentTypeId = null,
    Guid? TermId = null,
    Guid? CreatedBy = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    int Top = SearchService.DefaultTop,
    int Skip = 0,
    bool WithFacets = true);

internal sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int Count, SearchFacets? Facets, SearchMode Mode);

/// <summary>
/// Search (SRC-01…04, SRC-07…09): full text, semantic or both (hybrid, fused with reciprocal rank fusion), always
/// trimmed to what the caller may read, with the passage (and page) that matched best.
/// </summary>
internal sealed class SearchService(
    SearchDbContext db, IFullTextSearch fullText, SemanticSearch semantic, ITermStore terms, IWorkspaceAccess workspaces,
    IUserDirectory users, ICurrentUser user, IOptions<SearchOptions> options)
{
    public const int DefaultTop = 25;
    public const int MaxTop = 100;

    /// <summary>The constant of reciprocal rank fusion: a document's score is the sum of 1 / (k + rank) over both lists.</summary>
    public const int FusionK = 60;

    private const int FacetSize = 20;
    private const int SnippetRadius = 80;

    public const string KeywordMatch = "keyword";
    public const string SemanticMatchName = "semantic";

    /// <summary>The result, or the parameter that is wrong and why.</summary>
    public async Task<(SearchResult? Result, string? Parameter, string? Error)> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var hasText = !string.IsNullOrWhiteSpace(request.Q);
        SearchMode mode;
        if (request.Mode is { } requested)
        {
            if (requested != SearchMode.Keyword && !semantic.Enabled)
            {
                return (null, "mode", "Semantic search is not configured on this server (AI:Embeddings).");
            }

            if (requested != SearchMode.Keyword && !hasText)
            {
                return (null, "q", "Semantic and hybrid search need a query.");
            }

            mode = requested;
        }
        else
        {
            mode = hasText && semantic.Enabled ? options.Value.DefaultMode ?? SearchMode.Hybrid : SearchMode.Keyword;
        }

        FullTextQuery? query = null;
        if (hasText)
        {
            query = FullTextQuery.Parse(request.Q, out var error);
            if (query is null && mode != SearchMode.Semantic)
            {
                return (null, "q", error);
            }
        }
        else if (request.WorkspaceId is null && request.ContainerId is null && request.ContentTypeId is null && request.TermId is null && request.CreatedBy is null)
        {
            return (null, "q", "Enter a query or at least one filter.");
        }

        var filtered = await FilteredAsync(request, ct);
        var result = mode == SearchMode.Keyword
            ? await KeywordAsync(request, query, filtered, ct)
            : await FusedAsync(request, mode, query, filtered, ct);
        return (result, null, null);
    }

    /// <summary>Documents the caller may read that match the filters.</summary>
    private async Task<IQueryable<SearchDocument>> FilteredAsync(SearchRequest request, CancellationToken ct)
    {
        var principals = await PrincipalsAsync(workspaces, users, user, ct);
        var documents = db.Documents.AsNoTracking().Where(d => db.Principals.Any(p => p.DocumentId == d.Id && principals.Contains(p.Principal)));
        if (request.WorkspaceId is { } ws)
        {
            documents = documents.Where(d => d.WorkspaceId == ws);
        }

        if (request.ContainerId is { } container)
        {
            documents = documents.Where(d => d.ContainerId == container);
        }

        if (request.ContentTypeId is { } contentType)
        {
            documents = documents.Where(d => d.ContentTypeId == contentType);
        }

        if (request.CreatedBy is { } author)
        {
            documents = documents.Where(d => d.CreatedBy == author);
        }

        if (request.UpdatedFrom is { } from)
        {
            documents = documents.Where(d => d.UpdatedAt >= from);
        }

        if (request.UpdatedTo is { } to)
        {
            documents = documents.Where(d => d.UpdatedAt < to);
        }

        if (request.TermId is { } term)
        {
            // Hierarchical: a term also matches documents tagged with its children.
            var subtree = (await terms.GetDescendantsAsync([term], ct)).GetValueOrDefault(term) ?? [term];
            documents = documents.Where(d => db.Tags.Any(t => t.DocumentId == d.Id && subtree.Contains(t.TermId)));
        }

        return documents;
    }

    /// <summary>Full text (or filters only): exact count and facets over every match, paged by rank.</summary>
    private async Task<SearchResult> KeywordAsync(SearchRequest request, FullTextQuery? query, IQueryable<SearchDocument> filtered, CancellationToken ct)
    {
        var hits = query is null
            ? filtered.Select(d => new { Document = d, Rank = 0.0 })
            : from d in filtered
              join m in fullText.Match<SearchDocument>(db, query) on d.Id equals m.Id
              select new { Document = d, m.Rank };

        var count = await hits.CountAsync(ct);
        var facets = request.WithFacets ? await FacetsAsync(hits.Select(h => h.Document), ct) : null;
        var ordered = query is null
            ? hits.OrderByDescending(h => h.Document.UpdatedAt).ThenBy(h => h.Document.Id)
            : hits.OrderByDescending(h => h.Rank).ThenBy(h => h.Document.Id);
        var page = await ordered.Skip(request.Skip).Take(request.Top).ToListAsync(ct);
        var matchedBy = query is null ? Array.Empty<string>() : [KeywordMatch];
        var ranked = page.Select(h => (h.Document, h.Rank, matchedBy)).ToList();
        return new SearchResult(await WithPassagesAsync(ranked, query, [], ct), count, facets, SearchMode.Keyword);
    }

    /// <summary>
    /// Semantic or hybrid: the best <see cref="SearchOptions.CandidateLimit"/> documents of each side, fused by reciprocal
    /// rank; count and facets cover the fused candidates.
    /// </summary>
    private async Task<SearchResult> FusedAsync(SearchRequest request, SearchMode mode, FullTextQuery? query, IQueryable<SearchDocument> filtered, CancellationToken ct)
    {
        var limit = Math.Max(1, options.Value.CandidateLimit);
        var keyword = new List<Guid>();
        if (mode == SearchMode.Hybrid && query is not null)
        {
            keyword = await (from d in filtered
                             join m in fullText.Match<SearchDocument>(db, query) on d.Id equals m.Id
                             orderby m.Rank descending, d.Id
                             select d.Id).Take(limit).ToListAsync(ct);
        }

        var semanticMatches = await SemanticAsync(request, query, filtered, limit, ct);
        var scores = new Dictionary<Guid, double>();
        void Fuse(IEnumerable<Guid> ids)
        {
            foreach (var (id, index) in ids.Select((id, i) => (id, i)))
            {
                scores[id] = scores.GetValueOrDefault(id) + (1.0 / (FusionK + index + 1));
            }
        }

        Fuse(keyword);
        Fuse(semanticMatches.Select(m => m.DocumentId));
        var ordered = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key).ToList();
        var pageIds = ordered.Skip(request.Skip).Take(request.Top).Select(s => s.Key).ToList();
        var documents = await db.Documents.AsNoTracking().Where(d => pageIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var keywordIds = keyword.ToHashSet();
        var semanticIds = semanticMatches.ToDictionary(m => m.DocumentId);
        var ranked = pageIds.Where(documents.ContainsKey).Select(id =>
        {
            string[] matchedBy = keywordIds.Contains(id) && semanticIds.ContainsKey(id) ? [KeywordMatch, SemanticMatchName]
                : keywordIds.Contains(id) ? [KeywordMatch] : [SemanticMatchName];
            return (documents[id], scores[id], matchedBy);
        }).ToList();

        SearchFacets? facets = null;
        if (request.WithFacets)
        {
            var all = ordered.Select(s => s.Key).ToList();
            facets = await FacetsAsync(db.Documents.AsNoTracking().Where(d => all.Contains(d.Id)), ct);
        }

        return new SearchResult(await WithPassagesAsync(ranked, query, semanticIds, ct), ordered.Count, facets, mode);
    }

    /// <summary>Semantic matches the caller may read that pass the filters and contain no excluded word, best first.</summary>
    private async Task<List<SemanticMatch>> SemanticAsync(SearchRequest request, FullTextQuery? query, IQueryable<SearchDocument> filtered, int limit, CancellationToken ct)
    {
        var matches = await semantic.SearchAsync(SemanticText(request.Q!), request.WorkspaceId, request.ContainerId, ct);
        if (matches.Count == 0)
        {
            return matches;
        }

        var candidates = matches.Select(m => m.DocumentId).ToList();
        var allowed = (await filtered.Where(d => candidates.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        if (query is { Excluded.Count: > 0 })
        {
            var excluded = new FullTextQuery([query.Excluded], []);
            allowed.ExceptWith(await fullText.Match<SearchDocument>(db, excluded).Where(m => candidates.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct));
        }

        return [.. matches.Where(m => allowed.Contains(m.DocumentId)).Take(limit)];
    }

    /// <summary>
    /// Hits with the passage that matched best: for keyword matches the passage with the best full-text rank, else the
    /// most similar one. The hit's snippet comes from that passage and it points to its page (SRC-09).
    /// </summary>
    private async Task<List<SearchHit>> WithPassagesAsync(
        List<(SearchDocument Document, double Rank, string[] MatchedBy)> ranked, FullTextQuery? query,
        Dictionary<Guid, SemanticMatch> semanticMatches, CancellationToken ct)
    {
        var ids = ranked.Select(r => r.Document.Id).ToList();
        var passages = new Dictionary<Guid, (int? Page, string Text)>();
        if (query is not null && ranked.Any(r => r.MatchedBy.Contains(KeywordMatch)))
        {
            var matches = await (from p in db.Passages.AsNoTracking()
                                 join m in fullText.Match<SearchPassage>(db, query) on p.Id equals m.Id
                                 where ids.Contains(p.DocumentId)
                                 select new { p.DocumentId, p.Ordinal, p.Page, p.Text, m.Rank }).ToListAsync(ct);
            foreach (var best in matches.GroupBy(m => m.DocumentId).Select(g => g.OrderByDescending(m => m.Rank).ThenBy(m => m.Ordinal).First()))
            {
                passages[best.DocumentId] = (best.Page, best.Text);
            }
        }

        var similar = ids.Where(id => !passages.ContainsKey(id) && semanticMatches.ContainsKey(id)).Select(id => semanticMatches[id].PassageId).ToList();
        if (similar.Count > 0)
        {
            foreach (var p in await db.Passages.AsNoTracking().Where(p => similar.Contains(p.Id)).Select(p => new { p.DocumentId, p.Page, p.Text }).ToListAsync(ct))
            {
                passages[p.DocumentId] = (p.Page, p.Text);
            }
        }

        var tokens = query?.PositiveTokens.ToList() ?? [];
        return ranked.Select(r =>
        {
            var d = r.Document;
            var passage = passages.GetValueOrDefault(d.Id);
            return new SearchHit(d.Id, d.SourceType, d.WorkspaceId, d.ContainerId, d.ContentTypeId, d.Title,
                Snippet(passage.Text ?? d.Body, tokens), r.Rank, d.CreatedBy, d.UpdatedAt)
            {
                Page = passage.Page,
                MatchedBy = r.MatchedBy,
            };
        }).ToList();
    }

    private async Task<SearchFacets> FacetsAsync(IQueryable<SearchDocument> documents, CancellationToken ct)
    {
        var ids = documents.Select(d => d.Id);
        return new SearchFacets(
            await FacetAsync(documents, d => d.WorkspaceId, ct),
            await FacetAsync(documents.Where(d => d.ContainerId != null), d => d.ContainerId!.Value, ct),
            await FacetAsync(documents.Where(d => d.ContentTypeId != null), d => d.ContentTypeId!.Value, ct),
            await FacetAsync(db.Tags.Where(t => ids.Contains(t.DocumentId)), t => t.TermId, ct));
    }

    /// <summary>The caller's principals: user, groups, and workspace memberships (owners also as owners).</summary>
    internal static async Task<List<string>> PrincipalsAsync(IWorkspaceAccess workspaces, IUserDirectory users, ICurrentUser user, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return [];
        }

        var principals = new List<string> { Contracts.SearchPrincipals.User(userId) };
        principals.AddRange((await users.GetGroupIdsAsync(userId, ct)).Select(Contracts.SearchPrincipals.Group));
        foreach (var membership in await workspaces.GetMyWorkspacesAsync(ct))
        {
            principals.Add(Contracts.SearchPrincipals.WorkspaceMember(membership.WorkspaceId));
            if (membership.Level == WorkspaceAccessLevel.Manage)
            {
                principals.Add(Contracts.SearchPrincipals.WorkspaceOwner(membership.WorkspaceId));
            }
        }

        return principals;
    }

    /// <summary>The most frequent values of <paramref name="key"/> with their counts.</summary>
    private static async Task<List<FacetValue>> FacetAsync<T>(IQueryable<T> source, Expression<Func<T, Guid>> key, CancellationToken ct) =>
        (await source.GroupBy(key)
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(v => v.Count).ThenBy(v => v.Key)
            .Take(FacetSize)
            .ToListAsync(ct))
        .Select(v => new FacetValue(v.Key, v.Count))
        .ToList();

    /// <summary>The query as meaning: operators, quotes and excluded words removed.</summary>
    internal static string SemanticText(string q)
    {
        var words = new List<string>();
        var skipNext = false;
        foreach (var word in q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (word is "OR")
            {
                continue;
            }

            if (word is "NOT")
            {
                skipNext = true;
                continue;
            }

            if (word.StartsWith('-'))
            {
                continue;
            }

            words.Add(word.Trim('"', '*'));
        }

        var text = string.Join(' ', words.Where(w => w.Length > 0));
        return text.Length > 0 ? text : q;
    }

    /// <summary>Text around the first matching word (or the start of the text).</summary>
    internal static string? Snippet(string text, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var position = tokens
            .Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, position - SnippetRadius);
        var end = Math.Min(text.Length, position + SnippetRadius);
        var snippet = text[start..end].ReplaceLineEndings(" ").Trim();
        return (start > 0 ? "…" : string.Empty) + snippet + (end < text.Length ? "…" : string.Empty);
    }
}
