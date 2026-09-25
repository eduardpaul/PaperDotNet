namespace PaperDotNet.Search.Contracts;

/// <summary>
/// A searchable document, pushed by the module that owns the content. <see cref="Principals"/>
/// are the principals that may read it (see <see cref="SearchPrincipals"/>); search only
/// returns documents sharing a principal with the caller (SRC-04).
/// </summary>
public sealed record SearchDocumentData(
    Guid Id,
    string SourceType,
    Guid WorkspaceId,
    Guid? ContainerId,
    Guid? ContentTypeId,
    string Title,
    string Body,
    IReadOnlyCollection<string> Principals,
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
}

/// <summary>The search index of the current tenant.</summary>
public interface ISearchIndex
{
    Task UpsertAsync(IReadOnlyCollection<SearchDocumentData> documents, CancellationToken cancellationToken);

    Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

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

/// <summary>A module that can push all of its content again (reindex, SRC-10).</summary>
public interface ISearchSource
{
    string SourceType { get; }

    /// <summary>Writes every document of this source to <paramref name="index"/>, reporting progress (0 to 1).</summary>
    Task ReindexAsync(ISearchIndex index, Func<double, Task> progress, CancellationToken cancellationToken);
}

/// <summary>Principal names stored with documents and derived for the caller.</summary>
public static class SearchPrincipals
{
    public static string User(Guid id) => $"u:{id:N}";

    public static string Group(Guid id) => $"g:{id:N}";

    /// <summary>Any member (visitor or higher) of the workspace.</summary>
    public static string WorkspaceMember(Guid workspaceId) => $"w:{workspaceId:N}";

    /// <summary>Workspace owners and administrators (full control).</summary>
    public static string WorkspaceOwner(Guid workspaceId) => $"o:{workspaceId:N}";
}
