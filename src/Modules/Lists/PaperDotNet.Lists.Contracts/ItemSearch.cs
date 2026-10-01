namespace PaperDotNet.Lists.Contracts;

/// <summary>Extra searchable content of an item, e.g. the text of its file or its comments.</summary>
/// <param name="Text">Text added to the item's search body (ignored when <see cref="Pages"/> is set).</param>
/// <param name="Language">Language of the text (e.g. <c>english</c>), or null.</param>
public sealed record ItemSearchContent(string Text, string? Language)
{
    /// <summary>The text page by page (page 1 first), e.g. of a PDF, so search can point to the matching page (SRC-09).</summary>
    public IReadOnlyList<string>? Pages { get; init; }
}

/// <summary>
/// Adds content to the search documents of list items (SRC-01): Documents contributes the text of files,
/// Collaboration the comments. Called in batches when items are indexed; call <see cref="IListItemStore.ReindexAsync"/>
/// when the content changed.
/// </summary>
public interface IItemSearchContributor
{
    Task<IReadOnlyDictionary<Guid, ItemSearchContent>> GetContentAsync(Guid tenantId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
}

/// <summary>
/// What a user may read across a tenant's lists (ADR-0035), for indexes kept outside Lists (search): documents of
/// <see cref="FullWorkspaces"/> (managed by the user), and documents of <see cref="Workspaces"/> (where the user is a
/// member) whose permission scope is in <see cref="Scopes"/>.
/// </summary>
public sealed record ReadableScopes(IReadOnlySet<Guid> FullWorkspaces, IReadOnlySet<Guid> Workspaces, IReadOnlySet<Guid> Scopes);

/// <summary>Item permissions for other modules (ADR-0035).</summary>
public interface IItemAccess
{
    Task<ReadableScopes> GetReadableAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}
