using PaperDotNet.Abstractions;

namespace PaperDotNet.Taxonomy.Contracts;

public sealed record TermSetInfo(Guid Id, Guid GroupId, string Name, bool IsOpen, bool IsKeywords);

/// <summary>
/// A term: <see cref="IsKeyword"/> is true for terms of the keywords set and for terms promoted
/// from it (they can be used in keywords fields, TAX-05).
/// </summary>
public sealed record TermInfo(Guid Id, Guid TermSetId, string Name, bool IsKeyword, bool IsDeprecated);

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

    /// <summary>The terms with these ids (unknown ids are omitted).</summary>
    Task<IReadOnlyList<TermInfo>> GetTermsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken);

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

/// <summary>A term of a <see cref="TermSetTemplate"/>, with its children.</summary>
public sealed record TermTemplate(string Name, IReadOnlyList<string>? Synonyms = null, IReadOnlyList<TermTemplate>? Children = null, string? Description = null);

/// <summary>
/// A term set to provision (TAX-11): from extensions (<c>IExtensionBuilder.AddTermSet</c>; <see cref="Key"/>
/// starts with the extension id) or from a CSV import. Provisioning is additive: missing terms and synonyms
/// are added, nothing is removed or renamed.
/// </summary>
public sealed record TermSetTemplate(string GroupName, string Name, IReadOnlyList<TermTemplate> Terms, string? Description = null, bool IsOpen = false)
{
    /// <summary>Stable key of a managed term set (found by key even after a rename); null for imports.</summary>
    public string? Key { get; init; }

    /// <summary>Set for term sets of extensions.</summary>
    public string? ExtensionId { get; init; }
}

/// <summary>What provisioning a term set did.</summary>
public sealed record TermSetProvisioningResult(Guid TermSetId, bool Created, int TermsCreated);

/// <summary>Provisions term sets from templates (TAX-11).</summary>
public interface ITermSetProvisioning
{
    Task<TermSetProvisioningResult> EnsureAsync(TermSetTemplate termSet, CancellationToken cancellationToken);

    /// <summary>Provisions the term sets of an extension (when a tenant enables it).</summary>
    Task ProvisionExtensionAsync(string extensionId, CancellationToken cancellationToken);
}
