using PaperDotNet.Abstractions;

namespace PaperDotNet.Taxonomy.Contracts;

public sealed record TermSetInfo(Guid Id, Guid GroupId, string Name, bool IsOpen, bool IsKeywords);

/// <summary>Read access and term resolution for other modules (e.g. managed metadata fields).</summary>
public interface ITermStore
{
    Task<TermSetInfo?> GetTermSetAsync(Guid termSetId, CancellationToken cancellationToken);

    /// <summary>The term set's path <c>Group/Set</c> (how templates reference term sets), or null.</summary>
    Task<string?> GetTermSetPathAsync(Guid termSetId, CancellationToken cancellationToken);

    /// <summary>The term set named <paramref name="setName"/> in the group <paramref name="groupName"/>, if any.</summary>
    Task<Guid?> FindTermSetAsync(string groupName, string setName, CancellationToken cancellationToken);

    /// <summary>The tenant's keywords (folksonomy) term set; created on first use.</summary>
    Task<TermSetInfo> GetKeywordsSetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a value to an assignable term of <paramref name="termSetId"/>:
    /// a term id (merged terms resolve to their target; deprecated terms are
    /// rejected) or a label (name, translated label or synonym, case-insensitive,
    /// must be unambiguous). When nothing matches, <paramref name="allowCreate"/>
    /// is true and the set is open, a new root term is created. Returns null when invalid.
    /// </summary>
    Task<Guid?> ResolveAsync(Guid termSetId, string value, bool allowCreate, CancellationToken cancellationToken);

    /// <summary>Name, translated labels and synonyms of each term (for search indexing).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetLabelsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken);

    /// <summary>Each term with its descendants (including itself); ids that are not terms are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetDescendantsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken);
}

/// <summary>A term was merged into another; stored references must be rewritten.</summary>
public sealed record TermMerged : IntegrationEvent
{
    public required Guid TermSetId { get; init; }

    public required Guid SourceTermId { get; init; }

    public required Guid TargetTermId { get; init; }
}
