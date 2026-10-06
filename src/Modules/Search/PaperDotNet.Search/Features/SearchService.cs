using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Taxonomy.Contracts;

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
    bool WithFacets = true,
    string? Filter = null);

internal sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int Count, SearchFacets? Facets, SearchMode Mode);

/// <summary>
/// Search (SRC-01…04, SRC-07…09): full text, semantic or both (hybrid), always trimmed to what the caller may read,
/// with the passage (and page) that matched best. Validates the request, resolves what the caller may read and the
/// child terms, embeds the query, and leaves finding and ranking to the <see cref="ISearchStore"/> (ADR-0043).
/// </summary>
internal sealed class SearchService(
    ISearchStore store, EmbeddingModel embeddings, ITermStore terms, IItemAccess access, IOptions<SearchOptions> options, SearchDbContext db)
{
    public const int DefaultTop = 25;

    /// <summary>Source type of list items (documents, tasks, notes…), whose hits are checked against the items as they are now.</summary>
    private const string ItemSourceType = "listItem";
    public const int MaxTop = 100;

    private const int SnippetRadius = 80;

    private static readonly SearchFacets NoFacets = new([], [], [], []);

    /// <summary>Whether semantic and hybrid search are available (a model is configured and the store has vectors).</summary>
    public bool SemanticEnabled => embeddings.Enabled && store.Capabilities.HasFlag(SearchStoreCapabilities.Vector);

    /// <summary>The result, or the parameter that is wrong and why.</summary>
    public async Task<(SearchResult? Result, string? Parameter, string? Error)> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var hasText = !string.IsNullOrWhiteSpace(request.Q);
        SearchMode mode;
        if (request.Mode is { } requested)
        {
            if (requested != SearchMode.Keyword && !SemanticEnabled)
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
            mode = hasText && SemanticEnabled ? options.Value.DefaultMode ?? SearchMode.Hybrid : SearchMode.Keyword;
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
        else if (request.WorkspaceId is null && request.ContainerId is null && request.ContentTypeId is null && request.TermId is null && request.CreatedBy is null
                 && string.IsNullOrWhiteSpace(request.Filter))
        {
            return (null, "q", "Enter a query or at least one filter.");
        }

        SearchFilter? fields = null;
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            try
            {
                fields = SearchFilterParser.Parse(request.Filter, await store.GetFieldsAsync(ct));
            }
            catch (ArgumentException ex)
            {
                return (null, "$filter", ex.Message);
            }
        }

        var storeQuery = new StoreSearchQuery(mode, query, await FilterAsync(request, fields, ct), request.Skip, request.Top)
        {
            MinSimilarity = options.Value.MinSimilarity,
            CandidateLimit = options.Value.CandidateLimit,
            WithFacets = request.WithFacets,
        };
        if (mode != SearchMode.Keyword)
        {
            storeQuery = storeQuery with { Vector = await embeddings.EmbedQueryAsync(SemanticText(request.Q!), ct), VectorModel = embeddings.ModelKey };
        }

        var found = await store.SearchAsync(storeQuery, ct);

        // The index catches up with deletes and permission changes asynchronously; the page shown is checked against the
        // items as they are now (bounded by the page size, never the caller's whole readable set).
        var itemHits = found.Hits.Where(h => h.SourceType == ItemSourceType).Select(h => h.Id).ToList();
        var readableItems = itemHits.Count == 0 ? null : await access.FilterReadableAsync(itemHits, ct);
        var visible = readableItems is null ? found.Hits : [.. found.Hits.Where(h => h.SourceType != ItemSourceType || readableItems.Contains(h.Id))];
        var tokens = query?.PositiveTokens.ToList() ?? [];
        var hits = visible.Select(h => new SearchHit(h.Id, h.SourceType, h.WorkspaceId, h.ContainerId, h.ContentTypeId, h.Title,
            Snippet(h.Text ?? string.Empty, tokens), h.Rank, h.CreatedBy, h.UpdatedAt)
        {
            Page = h.Page,
            MatchedBy = h.MatchedBy,
        }).ToList();
        var facets = found.Facets ?? (request.WithFacets ? NoFacets : null);
        return (new SearchResult(hits, Math.Max(0, found.Count - (found.Hits.Count - visible.Count)), facets, mode), null, null);
    }

    /// <summary>The filters of the request, trimmed to the scopes the caller can read in the whole tenant (ADR-0035).</summary>
    private async Task<StoreFilter> FilterAsync(SearchRequest request, SearchFilter? fields, CancellationToken ct)
    {
        var readable = (await access.GetScopesAsync(null, ct)).Keys.ToArray();
        IReadOnlyCollection<Guid>? termIds = null;
        if (request.TermId is { } term)
        {
            // Hierarchical: a term also matches documents tagged with its children.
            termIds = (await terms.GetDescendantsAsync([term], ct)).GetValueOrDefault(term) ?? [term];
        }

        return new StoreFilter(readable)
        {
            ExcludedContainers = await db.ContainerPolicies.AsNoTracking().Where(p => !p.Included).Select(p => p.Id).ToArrayAsync(ct),
            WorkspaceId = request.WorkspaceId,
            ContainerId = request.ContainerId,
            ContentTypeId = request.ContentTypeId,
            TermIds = termIds,
            CreatedBy = request.CreatedBy,
            UpdatedFrom = request.UpdatedFrom,
            UpdatedTo = request.UpdatedTo,
            Fields = fields,
        };
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
