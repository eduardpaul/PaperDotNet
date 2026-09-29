using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Data;

/// <summary>A reusable, tenant-wide schema: an ordered set of fields (SharePoint content type).</summary>
public sealed class ContentType : ITenantOwned, IAuditable, IVersioned
{
    public const string ItemName = "Item";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Provided by the system (or an extension); cannot be deleted.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Key of the <see cref="ContentTypeTemplate"/> it was provisioned from (e.g. <c>task</c>).</summary>
    public string? Key { get; set; }

    /// <summary>Extension that manages the content type (tenants cannot change it).</summary>
    public string? ExtensionId { get; set; }

    public List<FieldDefinition> Fields { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

public enum ListKind
{
    /// <summary>Structured items only.</summary>
    List = 0,

    /// <summary>Items that carry files (documents, from phase 3).</summary>
    Library = 1,
}

/// <summary>Version history of a list (LST-11). Minor versions (drafts) come with documents.</summary>
public enum ListVersioning
{
    Off = 0,

    /// <summary>Every change creates a version.</summary>
    Major = 1,
}

/// <summary>A list or library in a workspace.</summary>
public sealed class ListDefinition : ITenantOwned, IAuditable, ISoftDeletable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public ListKind Kind { get; set; }

    public bool AllowFolders { get; set; } = true;

    /// <summary>Content types allowed in the list; the first is the default.</summary>
    public List<Guid> ContentTypeIds { get; set; } = [];

    public ListVersioning Versioning { get; set; }

    /// <summary>Key of the list template the list was created from, if any (LST-16).</summary>
    public string? TemplateKey { get; set; }

    /// <summary>
    /// The list has its own permissions instead of the workspace roles' (IAM-07). Either way the list is a
    /// permission scope with <see cref="AclEntry"/> rows.
    /// </summary>
    public bool HasUniquePermissions { get; set; }

    /// <summary>Marks lists created by the system, e.g. <see cref="HomeInboxKey"/>; they cannot be deleted.</summary>
    public string? SystemKey { get; set; }

    public const string HomeInboxKey = "home.inbox";
    public const string HomeDocumentsKey = "home.documents";

    /// <summary>Versions kept per item; older ones are removed.</summary>
    public int MaxVersions { get; set; } = DefaultMaxVersions;

    public const int DefaultMaxVersions = 50;
    public const int MaxVersionsLimit = 500;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public uint Version { get; set; }
}

/// <summary>An item (or folder) in a list. Field values live in one JSON document.</summary>
public sealed class ListItem : ITenantOwned, IAuditable, ISoftDeletable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public Guid ContentTypeId { get; set; }

    /// <summary>Containing folder, or null at the list root.</summary>
    public Guid? ParentId { get; set; }

    public bool IsFolder { get; set; }

    /// <summary>The item has its own permissions (it is then its own <see cref="ScopeId"/>).</summary>
    public bool HasUniquePermissions { get; set; }

    /// <summary>
    /// Permission scope (ADR-0035): the nearest item (this one or a folder above it) with unique permissions, or the
    /// list id when permissions come from the list. Never empty: queries trim items with one <c>IN</c> filter on it.
    /// </summary>
    public Guid ScopeId { get; set; }

    /// <summary>The built-in <c>title</c> field, kept as a column for display, sorting and search.</summary>
    public required string Title { get; set; }

    /// <summary>All other field values as a JSON object (normalized by their field types).</summary>
    public string Fields { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public uint Version { get; set; }
}

/// <summary>
/// A snapshot of an item after a change (LST-11/12). Numbered from 1 per item;
/// written in the same transaction as the change.
/// </summary>
[NotAudited]
public sealed class ItemVersion : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public Guid ListId { get; set; }

    public int Number { get; set; }

    public Guid ContentTypeId { get; set; }

    public required string Title { get; set; }

    /// <summary>Field values (without <c>title</c>) as JSON, like <see cref="ListItem.Fields"/>.</summary>
    public string Fields { get; set; } = "{}";

    /// <summary>Fields changed compared with the previous version (all fields for the first).</summary>
    public List<string> ChangedFields { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}

/// <summary>Who a permission entry gives access to: a user, a group, or a role of the list's workspace.</summary>
public enum AclPrincipalType
{
    User = 0,
    Group = 1,

    /// <summary>Visitors of the list's workspace (a role principal, see <see cref="WorkspaceRolePrincipals"/>).</summary>
    WorkspaceVisitors = 2,

    /// <summary>Members of the list's workspace.</summary>
    WorkspaceMembers = 3,

    /// <summary>Owners of the list's workspace. Every scope has this entry with Manage (full control).</summary>
    WorkspaceOwners = 4,
}

/// <summary>
/// One entry of a permission scope's access list (IAM-07, ADR-0035): a principal and its level on the items of the
/// scope. The scope is a list (<see cref="ListDefinition.Id"/>) or an item with unique permissions. Inheriting
/// lists have entries for the three workspace roles, so workspace membership changes write nothing here.
/// </summary>
[NotAudited]
public sealed class AclEntry : ITenantOwned
{
    public Guid ScopeId { get; set; }

    /// <summary>A user, a group, or a role principal (<see cref="WorkspaceRolePrincipals.Id"/>).</summary>
    public Guid PrincipalId { get; set; }

    public AclPrincipalType PrincipalType { get; set; }

    /// <summary>Read, Contribute or Manage; stored as a number, so queries compare levels.</summary>
    public WorkspaceAccessLevel Level { get; set; }

    public Guid ListId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid TenantId { get; set; }
}

public enum ViewLayout
{
    Table = 0,
    Board = 1,
    Calendar = 2,
    Gallery = 3,
}

/// <summary>A saved way to look at a list: columns, filter, order and layout.</summary>
public sealed class ListView : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public required string Name { get; set; }

    public List<string> Columns { get; set; } = [];

    /// <summary>OData <c>$filter</c> expression.</summary>
    public string? Filter { get; set; }

    /// <summary>OData <c>$orderby</c> expression.</summary>
    public string? OrderBy { get; set; }

    /// <summary>Field to group by (board columns, calendar date, …).</summary>
    public string? GroupBy { get; set; }

    public ViewLayout Layout { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

public enum ItemChangeKind
{
    /// <summary>The item was added, changed or restored.</summary>
    Upserted = 0,

    /// <summary>The item was moved to the recycle bin or purged.</summary>
    Deleted = 1,

    /// <summary>Permissions of the list changed: delta clients must sync again.</summary>
    Reset = 2,
}

/// <summary>
/// One entry of a list's change log (API-05), written in the same transaction as the change.
/// The sequence orders changes; delta tokens point into it.
/// </summary>
[NotAudited]
public sealed class ItemChange : ITenantOwned
{
    public long Sequence { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>Null for <see cref="ItemChangeKind.Reset"/>.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>The item's security scope at the time of the change (checks access after a purge).</summary>
    public Guid? ScopeId { get; set; }

    public ItemChangeKind Kind { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>
/// A smart folder (TAX-08…10): a saved, rule-based view over items of many lists. Personal (<see cref="OwnerId"/>)
/// or shared in a workspace; the definition is JSON (<c>SmartFolderDefinition</c>).
/// </summary>
public sealed class SmartFolder : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The workspace of a shared folder, or the scope of a personal one (null = all of the owner's workspaces).</summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>Set for personal folders.</summary>
    public Guid? OwnerId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public string Definition { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}
