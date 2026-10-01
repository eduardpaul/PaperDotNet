namespace PaperDotNet.Search.Contracts;

/// <summary>
/// A searchable document, pushed by the module that owns the content. <see cref="ScopeId"/> is its permission scope
/// (ADR-0035): search only returns documents whose scope the caller can read (SRC-04), so permission changes do not
/// touch the text.
/// </summary>
public sealed record SearchDocumentData(
    Guid Id,
    string SourceType,
    Guid WorkspaceId,
    Guid? ContainerId,
    Guid? ContentTypeId,
    string Title,
    string Body,
    Guid ScopeId,
    IReadOnlyCollection<Guid> TermIds,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt)
{
    /// <summary>High-weight text (ranks between title and body), e.g. tags or fields marked as important (SRC-06).</summary>
    public string Keywords { get; init; } = string.Empty;

    /// <summary>
    /// Language of the text (SRC-05), e.g. <c>english</c>; kept for providers that stem per language (SQLite stems
    /// English for all documents).
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Text with page numbers (page 1 first), e.g. of a document's file: searched like the body, and hits point to
    /// the matching page (SRC-09).
    /// </summary>
    public IReadOnlyList<string> Pages { get; init; } = [];
}

/// <summary>The search index. The tenant is named in every call (no ambient tenant, ADR-0039).</summary>
public interface ISearchIndex
{
    Task UpsertAsync(Guid tenantId, IReadOnlyCollection<SearchDocumentData> documents, CancellationToken cancellationToken);

    Task DeleteAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Moves documents to other permission scopes (document id → scope id), keeping their text and passages. Unknown ids
    /// are skipped.
    /// </summary>
    Task SetScopesAsync(Guid tenantId, IReadOnlyDictionary<Guid, Guid> scopes, CancellationToken cancellationToken);

    /// <summary>Removes every document of a container (e.g. a list), before re-indexing it or when it is deleted.</summary>
    Task DeleteContainerAsync(Guid tenantId, Guid containerId, CancellationToken cancellationToken);

    Task DeleteSourceAsync(Guid tenantId, string sourceType, CancellationToken cancellationToken);
}

/// <summary>How often terms are used as tags in a tenant's search index (e.g. popular keywords, TAX-05).</summary>
public interface ITermUsage
{
    /// <summary>Number of indexed items tagged with each term (terms without use are omitted).</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountAsync(Guid tenantId, IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken);
}

/// <summary>A module that can push all of its content again (reindex, SRC-10).</summary>
public interface ISearchSource
{
    string SourceType { get; }

    /// <summary>Writes every document of this source in the tenant to <paramref name="index"/>, reporting progress (0 to 1).</summary>
    Task ReindexAsync(Guid tenantId, ISearchIndex index, Func<double, Task> progress, CancellationToken cancellationToken);
}
