using System.Text.Json.Serialization;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>
/// A search result. <see cref="Snippet"/> comes from the passage that matched best and <see cref="Page"/> is its page
/// (SRC-09; null when the match is not on a page). <see cref="MatchedBy"/> says how it was found (<c>keyword</c>).
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

/// <summary>Results; <c>mode</c> is how they were found (<c>keyword</c>; semantic and hybrid search come later).</summary>
public sealed record SearchResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<SearchHit> Value,
    [property: JsonPropertyName("@odata.count")] int Count,
    [property: JsonPropertyName("facets")] SearchFacets Facets,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink,
    [property: JsonPropertyName("mode")] string Mode);

/// <summary>What to search for; see <c>GET /v1.0/search</c>.</summary>
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
    bool WithFacets = true);

internal sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int Count, SearchFacets? Facets);

/// <summary>Keyword search (SRC-01…04, SRC-09), always trimmed to what the caller may read, with the passage (and page) that matched best.</summary>
internal sealed class SearchService(ISearchQueries queries, ITermStore terms, IItemAccess access)
{
    public const int DefaultTop = 25;
    public const int MaxTop = 100;
    public const string KeywordMatch = "keyword";
    private const int SnippetRadius = 80;

    /// <summary>The result, or the parameter that is wrong and why. A null <paramref name="userId"/> searches everything (system).</summary>
    public async Task<(SearchResult? Result, string? Parameter, string? Error)> SearchAsync(Guid tenantId, Guid? userId, SearchRequest request, CancellationToken ct)
    {
        FullTextQuery? query = null;
        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            query = FullTextQuery.Parse(request.Q, out var error);
            if (query is null)
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
        var match = query?.ToFts5();
        var page = await queries.SearchAsync(filter, match, request.Top, request.Skip, request.WithFacets, ct);

        // The best passage of each hit gives its snippet and page.
        var passages = match is null
            ? []
            : (await queries.PassagesAsync(tenantId, match, [.. page.Rows.Select(r => r.Id)], ct))
                .GroupBy(p => p.DocumentId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Rank).ThenBy(p => p.Ordinal).First());
        var tokens = query?.PositiveTokens.ToList() ?? [];
        string[] matchedBy = query is null ? [] : [KeywordMatch];
        var hits = page.Rows.Select(r =>
        {
            var passage = passages.GetValueOrDefault(r.Id);
            return new SearchHit(r.Id, r.SourceType, r.WorkspaceId, r.ContainerId, r.ContentTypeId, r.Title,
                Snippet(passage?.Text ?? r.Body, tokens), r.Rank, r.CreatedBy, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt))
            {
                Page = passage?.Page,
                MatchedBy = matchedBy,
            };
        }).ToList();
        return (new SearchResult(hits, page.Count, page.Facets), null, null);
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
