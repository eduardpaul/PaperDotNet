using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>
/// A search result. <see cref="Snippet"/> comes from the passage that matched best and <see cref="Page"/> is its page
/// (SRC-09; null when the match is not on a page). <see cref="MatchedBy"/> says how it was found (<c>keyword</c>, <c>semantic</c>).
/// </summary>
public sealed record SearchHit(
    Guid Id,
    string SourceType,
    Guid WorkspaceId,
    Guid? ContainerId,
    Guid? ContentTypeId,
    string Title,
    string? Snippet,
    double Rank,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt)
{
    public int? Page { get; init; }

    public IReadOnlyList<string> MatchedBy { get; init; } = [];
}

public sealed record FacetValue(Guid Value, int Count);

public sealed record SearchFacets(
    IReadOnlyList<FacetValue> Workspace, IReadOnlyList<FacetValue> Container, IReadOnlyList<FacetValue> ContentType, IReadOnlyList<FacetValue> Term);

/// <summary>Results; <c>mode</c> is how they were found (<c>keyword</c>, <c>semantic</c> or <c>hybrid</c>).</summary>
public sealed record SearchResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<SearchHit> Value,
    [property: JsonPropertyName("@odata.count")] int Count,
    [property: JsonPropertyName("facets")] SearchFacets Facets,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink,
    [property: JsonPropertyName("mode")] string Mode);

/// <summary>What to search for; see <c>GET /v1.0/search</c>. A null <see cref="Mode"/> picks the server's default.</summary>
internal sealed record SearchRequest(
    string? Q,
    Guid? WorkspaceId = null,
    Guid? ContainerId = null,
    Guid? ContentTypeId = null,
    Guid? TermId = null,
    Guid? CreatedBy = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    int Top = SearchService.DefaultTop,
    int Skip = 0,
    bool WithFacets = true,
    string? Mode = null);

internal sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int Count, SearchFacets? Facets, string Mode);

/// <summary>
/// Search (SRC-01…04, SRC-07…09): full text, semantic or both (hybrid, fused with reciprocal rank fusion), always
/// trimmed to what the caller may read, with the passage (and page) that matched best.
/// </summary>
internal sealed class SearchService(ISearchQueries queries, SemanticSearch semantic, ITermStore terms, IItemAccess access, IOptions<SearchOptions> options)
{
    public const int DefaultTop = 25;
    public const int MaxTop = 100;
    public const string KeywordMatch = SearchModes.Keyword;
    public const string SemanticMatch = SearchModes.Semantic;

    /// <summary>The constant of reciprocal rank fusion: a document's score is the sum of 1 / (k + rank) over both lists.</summary>
    public const int FusionK = 60;

    private const int SnippetRadius = 80;

    /// <summary>The result, or the parameter that is wrong and why. A null <paramref name="userId"/> searches everything (system).</summary>
    public async Task<(SearchResult? Result, string? Parameter, string? Error)> SearchAsync(Guid tenantId, Guid? userId, SearchRequest request, CancellationToken ct)
    {
        var hasText = !string.IsNullOrWhiteSpace(request.Q);
        string mode;
        if (request.Mode is { Length: > 0 } requested)
        {
            if (SearchModes.Parse(requested) is not { } parsed)
            {
                return (null, "mode", "Use keyword, semantic or hybrid.");
            }

            if (parsed != SearchModes.Keyword && !semantic.Enabled)
            {
                return (null, "mode", "Semantic search is not configured on this server (AI:Embeddings).");
            }

            if (parsed != SearchModes.Keyword && !hasText)
            {
                return (null, "q", "Semantic and hybrid search need a query.");
            }

            mode = parsed;
        }
        else
        {
            mode = hasText && semantic.Enabled ? SearchModes.Parse(options.Value.DefaultMode) ?? SearchModes.Hybrid : SearchModes.Keyword;
        }

        FullTextQuery? query = null;
        if (hasText)
        {
            query = FullTextQuery.Parse(request.Q, out var error);
            if (query is null && mode != SearchModes.Semantic)
            {
                return (null, "q", error);
            }
        }
        else if (request.WorkspaceId is null && request.ContainerId is null && request.ContentTypeId is null && request.TermId is null && request.CreatedBy is null)
        {
            return (null, "q", "Enter a query or at least one filter.");
        }

        IReadOnlyCollection<Guid>? subtree = null;
        if (request.TermId is { } term)
        {
            // Hierarchical: a term also matches documents tagged with its children.
            subtree = (await terms.GetDescendantsAsync(tenantId, [term], ct)).GetValueOrDefault(term) ?? [term];
        }

        var readable = userId is { } user ? await access.GetReadableAsync(tenantId, user, ct) : null;
        var filter = new SearchFilter(
            tenantId, readable, request.WorkspaceId, request.ContainerId, request.ContentTypeId, subtree, request.CreatedBy,
            request.UpdatedFrom?.ToUnixTimeMilliseconds(), request.UpdatedTo?.ToUnixTimeMilliseconds());
        var result = mode == SearchModes.Keyword
            ? await KeywordAsync(filter, request, query, ct)
            : await FusedAsync(filter, request, mode, query, ct);
        return (result, null, null);
    }

    /// <summary>Full text (or filters only): exact count and facets over every match, paged by rank.</summary>
    private async Task<SearchResult> KeywordAsync(SearchFilter filter, SearchRequest request, FullTextQuery? query, CancellationToken ct)
    {
        var match = query?.ToFts5();
        var page = await queries.SearchAsync(filter, match, request.Top, request.Skip, request.WithFacets, ct);
        string[] matchedBy = query is null ? [] : [KeywordMatch];
        var ranked = page.Rows.Select(r => (r, r.Rank, matchedBy)).ToList();
        return new SearchResult(await WithPassagesAsync(filter.TenantId, ranked, query, [], ct), page.Count, page.Facets, SearchModes.Keyword);
    }

    /// <summary>
    /// Semantic or hybrid: the best <see cref="SearchOptions.CandidateLimit"/> documents of each side, fused by reciprocal
    /// rank; count and facets cover the fused candidates.
    /// </summary>
    private async Task<SearchResult> FusedAsync(SearchFilter filter, SearchRequest request, string mode, FullTextQuery? query, CancellationToken ct)
    {
        var limit = Math.Max(1, options.Value.CandidateLimit);
        List<Guid> keyword = [];
        if (mode == SearchModes.Hybrid && query is not null)
        {
            keyword = [.. (await queries.SearchAsync(filter, query.ToFts5(), limit, 0, false, ct)).Rows.Select(r => r.Id)];
        }

        var semanticMatches = await SemanticAsync(filter, request, query, limit, ct);
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
        var ordered = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key).Select(s => s.Key).ToList();
        var pageIds = ordered.Skip(request.Skip).Take(request.Top).ToList();
        var rows = pageIds.Count == 0
            ? []
            : (await queries.SearchAsync(filter with { Ids = pageIds }, null, pageIds.Count, 0, false, ct)).Rows.ToDictionary(r => r.Id);
        var keywordIds = keyword.ToHashSet();
        var semanticIds = semanticMatches.ToDictionary(m => m.DocumentId);
        var ranked = pageIds.Where(rows.ContainsKey).Select(id =>
        {
            string[] matchedBy = keywordIds.Contains(id) && semanticIds.ContainsKey(id) ? [KeywordMatch, SemanticMatch]
                : keywordIds.Contains(id) ? [KeywordMatch] : [SemanticMatch];
            return (rows[id], scores[id], matchedBy);
        }).ToList();

        SearchFacets? facets = null;
        if (request.WithFacets)
        {
            facets = ordered.Count == 0
                ? new SearchFacets([], [], [], [])
                : (await queries.SearchAsync(filter with { Ids = ordered }, null, 0, 0, true, ct)).Facets;
        }

        return new SearchResult(await WithPassagesAsync(filter.TenantId, ranked, query, semanticIds, ct), ordered.Count, facets, mode);
    }

    /// <summary>Semantic matches the caller may read that pass the filters and contain no excluded word, best first.</summary>
    private async Task<List<SemanticMatch>> SemanticAsync(SearchFilter filter, SearchRequest request, FullTextQuery? query, int limit, CancellationToken ct)
    {
        var matches = await semantic.SearchAsync(filter.TenantId, SemanticText(request.Q!), request.WorkspaceId, request.ContainerId, ct);
        if (matches.Count == 0)
        {
            return matches;
        }

        var candidates = filter with { Ids = [.. matches.Select(m => m.DocumentId)] };
        var allowed = (await queries.SearchAsync(candidates, null, matches.Count, 0, false, ct)).Rows.Select(r => r.Id).ToHashSet();
        if (query is { Excluded.Count: > 0 })
        {
            var excluded = new FullTextQuery([query.Excluded], []).ToFts5();
            allowed.ExceptWith((await queries.SearchAsync(candidates, excluded, matches.Count, 0, false, ct)).Rows.Select(r => r.Id));
        }

        return [.. matches.Where(m => allowed.Contains(m.DocumentId)).Take(limit)];
    }

    /// <summary>
    /// Hits with the passage that matched best: for keyword matches the passage with the best full-text rank, else the
    /// most similar one. The hit's snippet comes from that passage and it points to its page (SRC-09).
    /// </summary>
    private async Task<List<SearchHit>> WithPassagesAsync(
        Guid tenantId, List<(SearchRow Row, double Rank, string[] MatchedBy)> ranked, FullTextQuery? query,
        Dictionary<Guid, SemanticMatch> semanticMatches, CancellationToken ct)
    {
        var passages = new Dictionary<Guid, PassageRow>();
        var keywordIds = ranked.Where(r => r.MatchedBy.Contains(KeywordMatch)).Select(r => r.Row.Id).ToList();
        if (query is not null && keywordIds.Count > 0)
        {
            foreach (var best in (await queries.PassagesAsync(tenantId, query.ToFts5(), keywordIds, ct))
                .GroupBy(p => p.DocumentId)
                .Select(g => g.OrderByDescending(p => p.Rank).ThenBy(p => p.Ordinal).First()))
            {
                passages[best.DocumentId] = best;
            }
        }

        var similar = ranked.Select(r => r.Row.Id)
            .Where(id => !passages.ContainsKey(id) && semanticMatches.ContainsKey(id))
            .Select(id => semanticMatches[id].PassageId)
            .ToList();
        foreach (var passage in await queries.PassagesByIdAsync(tenantId, similar, ct))
        {
            passages[passage.DocumentId] = passage;
        }

        var tokens = query?.PositiveTokens.ToList() ?? [];
        return ranked.Select(h =>
        {
            var r = h.Row;
            var passage = passages.GetValueOrDefault(r.Id);
            return new SearchHit(r.Id, r.SourceType, r.WorkspaceId, r.ContainerId, r.ContentTypeId, r.Title,
                Snippet(passage?.Text ?? r.Body, tokens), h.Rank, r.CreatedBy, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt))
            {
                Page = passage?.Page,
                MatchedBy = h.MatchedBy,
            };
        }).ToList();
    }

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
