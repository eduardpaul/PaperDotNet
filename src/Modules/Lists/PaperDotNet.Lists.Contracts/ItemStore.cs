using System.Text.Json.Nodes;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Contracts;

/// <summary>A list, as seen by <see cref="IListItemStore"/>.</summary>
public sealed record ListData(Guid Id, Guid WorkspaceId, string Name, string? TemplateKey)
{
    /// <summary>A document library (items carry files).</summary>
    public bool IsLibrary { get; init; }

    /// <summary>The caller's access to the list (set by <see cref="IListItemStore.GetListAsync"/>).</summary>
    public WorkspaceAccessLevel? Access { get; init; }

    /// <summary>Template keys of the list's content types (e.g. <c>task</c>); custom content types have none.</summary>
    public IReadOnlyList<string> ContentTypeKeys { get; init; } = [];

    /// <summary>The list's content types (the first is the default).</summary>
    public IReadOnlyList<ListContentType> ContentTypes { get; init; } = [];
}

/// <summary>A content type of a list: its name, and its template key for built-in and extension ones.</summary>
public sealed record ListContentType(Guid Id, string Name, string? Key);

/// <summary>The caller's personal workspace with its Documents and Inbox libraries (LST-07).</summary>
public sealed record HomeData(Guid WorkspaceId, Guid DocumentsListId, Guid InboxListId);

/// <summary>An item: <see cref="Fields"/> holds every value, including <c>title</c>.</summary>
public sealed record ListItemData(
    Guid Id,
    Guid WorkspaceId,
    Guid ListId,
    Guid ContentTypeId,
    Guid? ParentId,
    bool IsFolder,
    uint Version,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedBy,
    JsonObject Fields)
{
    /// <summary>The caller's access to the item (Read or more; Contribute allows changes).</summary>
    public WorkspaceAccessLevel Access { get; init; } = WorkspaceAccessLevel.Read;
}

/// <summary>An item query: OData <c>$filter</c>/<c>$orderby</c> over the list's fields (as in the items API).</summary>
public sealed record ListItemQuery(string? Filter = null, string? OrderBy = null, int Top = 100);

public enum ListItemStatus
{
    Ok,

    /// <summary>The list or item does not exist or is not visible.</summary>
    NotFound,

    /// <summary>Visible, but the caller may not change it.</summary>
    Forbidden,

    /// <summary>Invalid values or query (<see cref="ListItemResult.Errors"/>).</summary>
    Invalid,

    /// <summary>The expected version did not match (someone else changed the item).</summary>
    VersionMismatch,

    /// <summary>A mutator cancelled the change, or the change conflicts (e.g. a non-empty folder).</summary>
    Rejected,
}

/// <summary>Outcome of a store operation.</summary>
public sealed record ListItemResult(ListItemStatus Status, ListItemData? Item = null, IReadOnlyDictionary<string, string[]>? Errors = null, string? Message = null)
{
    public bool Succeeded => Status == ListItemStatus.Ok;
}

/// <summary>
/// Reads and writes list items from code (extensions, jobs). Writes go through the same
/// pipeline as the API: field validation, mutators, versions, events, search.
/// By default the store acts as the current user with their permissions (ADR-0011); use
/// <see cref="AsSystem"/> for background work that acts on behalf of the organization.
/// </summary>
public interface IListItemStore
{
    /// <summary>
    /// A store with full control over every list of the current tenant: permissions of the caller are
    /// not checked (events still name the current user, if any).
    /// </summary>
    IListItemStore AsSystem();

    /// <summary>Lists the caller can see, optionally in one workspace and created from one template.</summary>
    Task<IReadOnlyList<ListData>> GetListsAsync(Guid? workspaceId, string? templateKey, CancellationToken cancellationToken);

    /// <summary>One list with the caller's access, or null when it is not visible.</summary>
    Task<ListData?> GetListAsync(Guid workspaceId, Guid listId, CancellationToken cancellationToken);

    /// <summary>The caller's Home workspace and libraries, created on first use (needs a user).</summary>
    Task<HomeData> EnsureHomeAsync(CancellationToken cancellationToken);

    Task<ListItemData?> GetAsync(Guid workspaceId, Guid listId, Guid itemId, CancellationToken cancellationToken);

    /// <summary>Items matching the query (up to <see cref="ListItemQuery.Top"/>, max 1000); folders are excluded.</summary>
    Task<(IReadOnlyList<ListItemData> Items, string? Error)> QueryAsync(Guid workspaceId, Guid listId, ListItemQuery query, CancellationToken cancellationToken);

    Task<ListItemResult> CreateAsync(Guid workspaceId, Guid listId, JsonObject fields, Guid? contentTypeId, CancellationToken cancellationToken);

    /// <summary>Merges <paramref name="fields"/> into the item (null removes a value); checks <paramref name="expectedVersion"/> when given.</summary>
    Task<ListItemResult> UpdateAsync(Guid workspaceId, Guid listId, Guid itemId, JsonObject fields, uint? expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// The folder at <paramref name="path"/> (folder titles from the list root), created where missing;
    /// null for an empty path (the list root). Fails when the list does not allow folders.
    /// </summary>
    Task<(Guid? FolderId, ListItemResult? Problem)> EnsureFolderAsync(Guid workspaceId, Guid listId, IReadOnlyList<string> path, CancellationToken cancellationToken);

    /// <summary>Moves the item (or folder) into <paramref name="folderId"/> (null: the list root) in the same list.</summary>
    Task<ListItemResult> MoveAsync(Guid workspaceId, Guid listId, Guid itemId, Guid? folderId, CancellationToken cancellationToken);

    /// <summary>Indexes the item again for search (e.g. after its <see cref="IItemSearchContributor"/> content changed).</summary>
    Task ReindexAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Moves the item to the recycle bin.</summary>
    Task<ListItemResult> DeleteAsync(Guid workspaceId, Guid listId, Guid itemId, uint? expectedVersion, CancellationToken cancellationToken);
}
