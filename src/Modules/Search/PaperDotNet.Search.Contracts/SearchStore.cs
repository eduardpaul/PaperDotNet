using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Search.Contracts;

/// <summary>How results are found (SRC-08).</summary>
public enum SearchMode
{
    /// <summary>Full-text search only.</summary>
    Keyword,

    /// <summary>Embeddings only: documents by meaning.</summary>
    Semantic,

    /// <summary>Both, fused with reciprocal rank fusion.</summary>
    Hybrid,
}

public sealed record FacetValue(Guid Value, int Count);

public sealed record SearchFacets(
    IReadOnlyList<FacetValue> Workspace, IReadOnlyList<FacetValue> Container, IReadOnlyList<FacetValue> ContentType, IReadOnlyList<FacetValue> Term);

/// <summary>What a store can do itself; the search service does the rest over the candidates.</summary>
[Flags]
public enum SearchStoreCapabilities
{
    None = 0,

    /// <summary>Full-text search (always needed).</summary>
    Keyword = 1,

    /// <summary>Semantic search over passage embeddings.</summary>
    Vector = 2,

    /// <summary>Exact counts and facets over every match.</summary>
    Facets = 4,
}

/// <summary>
/// Filters of a search. <see cref="ReadableScopes"/> trims to what the caller may read (ADR-0035) and is always
/// applied; <see cref="TermIds"/> already contains the child terms of the requested term.
/// </summary>
public sealed record StoreFilter(IReadOnlyCollection<Guid> ReadableScopes)
{
    public Guid? WorkspaceId { get; init; }

    public Guid? ContainerId { get; init; }

    public Guid? ContentTypeId { get; init; }

    public IReadOnlyCollection<Guid>? TermIds { get; init; }

    public Guid? CreatedBy { get; init; }

    public DateTimeOffset? UpdatedFrom { get; init; }

    public DateTimeOffset? UpdatedTo { get; init; }
}

/// <summary>
/// One search, already validated: <see cref="Text"/> is required for keyword and hybrid search with words (filters
/// alone list documents by date), <see cref="Vector"/> (normalized, of <see cref="VectorModel"/>) for semantic and
/// hybrid search.
/// </summary>
public sealed record StoreSearchQuery(SearchMode Mode, FullTextQuery? Text, StoreFilter Filter, int Skip, int Top)
{
    public ReadOnlyMemory<float>? Vector { get; init; }

    public string? VectorModel { get; init; }

    /// <summary>Passages less similar than this (cosine) are no semantic match.</summary>
    public float MinSimilarity { get; init; }

    /// <summary>Documents taken from each side before fusion (and at most returned by semantic search).</summary>
    public int CandidateLimit { get; init; } = 200;

    public bool WithFacets { get; init; }
}

/// <summary>
/// A matching document. <see cref="Text"/> is the text of the passage that matched best (or the document's text) to
/// cut the snippet from, and <see cref="Page"/> the page of that passage (SRC-09).
/// </summary>
public sealed record StoreHit(
    Guid Id,
    string SourceType,
    Guid WorkspaceId,
    Guid? ContainerId,
    Guid? ContentTypeId,
    string Title,
    double Rank,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt)
{
    public int? Page { get; init; }

    public string? Text { get; init; }

    /// <summary><c>keyword</c>, <c>semantic</c> or both.</summary>
    public IReadOnlyList<string> MatchedBy { get; init; } = [];
}

/// <summary>A page of hits; <see cref="Count"/> covers every match (keyword) or every fused candidate.</summary>
public sealed record StoreSearchResult(IReadOnlyList<StoreHit> Hits, int Count, SearchFacets? Facets);

/// <summary>A passage without an embedding of the current model, with the text to embed.</summary>
public sealed record PassageToEmbed(Guid Id, string Input);

/// <summary>The embedding of a passage (any length; the store normalizes it).</summary>
public sealed record PassageEmbedding(Guid PassageId, ReadOnlyMemory<float> Vector);

/// <summary>
/// Where search documents live and how they are found (ADR-0043): the database tables by default, or another engine.
/// The search service, the API and the MCP tool only talk to this; producers write through <see cref="ISearchIndex"/>,
/// which is the store's write side.
/// </summary>
public interface ISearchStore : ISearchIndex
{
    /// <summary>The configured name (<c>Search:Store</c>), e.g. <c>database</c>.</summary>
    string Name { get; }

    SearchStoreCapabilities Capabilities { get; }

    Task<StoreSearchResult> SearchAsync(StoreSearchQuery query, CancellationToken cancellationToken);

    /// <summary>Number of indexed documents tagged with each term (terms without use are omitted).</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountTermsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="limit"/> passages without an embedding of <paramref name="model"/>.</summary>
    Task<IReadOnlyList<PassageToEmbed>> GetPassagesToEmbedAsync(string model, int limit, CancellationToken cancellationToken);

    /// <summary>Stores embeddings of <paramref name="model"/>; unknown passages are skipped.</summary>
    Task SetEmbeddingsAsync(string model, IReadOnlyCollection<PassageEmbedding> embeddings, CancellationToken cancellationToken);
}

/// <summary>How a hit was found (<see cref="StoreHit.MatchedBy"/>) and how hybrid results are fused.</summary>
public static class SearchMatch
{
    public const string Keyword = "keyword";
    public const string Semantic = "semantic";

    /// <summary>The constant of reciprocal rank fusion: a document's score is the sum of 1 / (k + rank) over both lists.</summary>
    public const int FusionK = 60;
}

public static class SearchStoreServiceCollectionExtensions
{
    /// <summary>
    /// Makes a search store available under <paramref name="name"/>; <c>Search:Store</c> picks the one in use
    /// (<c>database</c> by default).
    /// </summary>
    public static IServiceCollection AddSearchStore<TStore>(this IServiceCollection services, string name)
        where TStore : class, ISearchStore =>
        services.AddKeyedScoped<ISearchStore, TStore>(name);
}
