namespace PaperDotNet.Search.Contracts;

/// <summary>
/// Source data and prepared chunks for workflow publication. <see cref="ScopeId"/> is its permission scope
/// (ADR-0035): search only returns documents whose scope the caller can read (SRC-04), while current item eligibility also protects queries during asynchronous scope updates.
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
    /// Language of the text, for stemming (SRC-05): a name like <c>english</c> or <c>german</c>
    /// (see <c>FullTextLanguages.FromCode</c> for ISO codes); null = exact words only.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Text with page numbers (page 1 first), e.g. of a document's file: searched like the body, and hits point to
    /// the matching page (SRC-09).
    /// </summary>
    public IReadOnlyList<string> Pages { get; init; } = [];

    /// <summary>
    /// The item's fields as typed search fields (ADR-0043): filterable and queryable with <c>$filter</c>. Their text is
    /// searched through <see cref="Body"/> and <see cref="Keywords"/>, as before.
    /// </summary>
    public IReadOnlyList<SearchField> Fields { get; init; } = [];

    /// <summary>Prepared by workflow activities; stores persist these chunks without choosing a chunker.</summary>
    public IReadOnlyList<PassageText> Chunks { get; init; } = [];

    /// <summary>The version of source file text, independent of metadata and search publication.</summary>
    public string? ContentRevision { get; init; }
    public bool ContentReady { get; init; } = true;
}

/// <summary>Current metadata of an item, built by its owner independently of index execution.</summary>
public interface ISearchItemSource
{
    /// <summary>The source type of its documents (e.g. <c>listItem</c>).</summary>
    string SourceType { get; }

    /// <summary>The item's search document (without file text), or null when it is gone or not searchable (a folder).</summary>
    Task<SearchDocumentData?> GetDocumentAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// A cheap stamp per item that changes whenever its document would (e.g. its last change), without building it: lets
    /// a sweep find stale publications without reading text. Gone items are left out.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetStampsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>The current permission scope of each item (gone items and folders are left out).</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> GetScopesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
}

/// <summary>Search triggers other modules raise; the Search module's workflows react to them.</summary>
public static class SearchTriggers
{
    /// <summary>An item's search data changed (e.g. a comment): indexed when automatic indexing is on.</summary>
    public const string Requested = "search.requested";

    /// <summary>A list's search inclusion or schema changed (data: <c>containerId</c>).</summary>
    public const string ContainerChanged = "search.containerChanged";

    /// <summary>Items moved to other permission scopes (data: <c>itemIds</c>); only their scopes are updated.</summary>
    public const string ScopesChanged = "search.scopesChanged";
}

/// <summary>Staged derived data. Only IDs and configuration, never this payload, are kept in workflow runs.</summary>
public sealed record SearchGeneration(Guid Id, SearchDocumentData Document, string Revision, uint PolicyVersion)
{
    /// <summary>The cheap source stamp at staging (<see cref="ISearchItemSource.GetStampsAsync"/> plus the text's revision).</summary>
    public string? Stamp { get; init; }

    /// <summary>The chunk settings it was made with (e.g. <c>window:1200:150</c>).</summary>
    public string? Settings { get; init; }

    /// <summary>Whether the text had more chunks than a document may have.</summary>
    public bool Truncated { get; init; }
}

/// <summary>The search index of the current tenant.</summary>
public interface ISearchIndex
{
    Task UpsertAsync(IReadOnlyCollection<SearchDocumentData> documents, CancellationToken cancellationToken);

    Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Moves documents to other permission scopes (document id → scope id), keeping their text, passages and
    /// embeddings. Unknown ids are skipped.
    /// </summary>
    Task SetScopesAsync(IReadOnlyDictionary<Guid, Guid> scopes, CancellationToken cancellationToken);

    /// <summary>Removes every document of a container (e.g. a list), before re-indexing it or when it is deleted.</summary>
    Task DeleteContainerAsync(Guid containerId, CancellationToken cancellationToken);

    Task DeleteSourceAsync(string sourceType, CancellationToken cancellationToken);
}

/// <summary>How often terms are used as tags in the tenant's search index (e.g. popular keywords, TAX-05).</summary>
public interface ITermUsage
{
    /// <summary>Number of indexed items tagged with each term (terms without use are omitted).</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken);
}
