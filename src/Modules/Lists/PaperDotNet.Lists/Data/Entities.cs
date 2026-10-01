using System.ComponentModel.DataAnnotations;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Lists.Data;

// Entity classes are not sealed (EF Core precompiled materializers, ADR-0039). Kinds are string constants (no enums
// stored) and structured values are JSON text read with source-generated metadata (ListsJson).
#pragma warning disable CA1852

/// <summary>A reusable, tenant-wide schema: an ordered set of fields (SharePoint content type).</summary>
public class ContentType : ITenantOwned, IAuditable, IVersioned
{
    public const string ItemName = "Item";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Provided by the system (or an extension); cannot be deleted.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Key of the <c>ContentTypeTemplate</c> it was provisioned from (e.g. <c>task</c>).</summary>
    public string? Key { get; set; }

    /// <summary>Extension that manages the content type (tenants cannot change it).</summary>
    public string? ExtensionId { get; set; }

    /// <summary>The fields as JSON (<c>FieldDefinition[]</c>, Lists.Contracts).</summary>
    public string Fields { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>Values of <see cref="ListDefinition.Kind"/>.</summary>
public static class ListKinds
{
    /// <summary>Structured items only.</summary>
    public const string List = "list";

    /// <summary>Items that carry files (documents).</summary>
    public const string Library = "library";

    public static bool IsValid(string? kind) => kind is List or Library;
}

/// <summary>Values of <see cref="ListDefinition.Versioning"/>: version history of a list (LST-11).</summary>
public static class ListVersionings
{
    public const string Off = "off";

    /// <summary>Every change creates a version.</summary>
    public const string Major = "major";

    public static bool IsValid(string? versioning) => versioning is Off or Major;
}

/// <summary>A list or library in a workspace. Deleting it moves it to the recycle bin.</summary>
public class ListDefinition : ITenantOwned, IAuditable, ISoftDeletable, IVersioned
{
    public const int DefaultMaxVersions = 50;
    public const int MaxVersionsLimit = 500;
    public const string HomeInboxKey = "home.inbox";
    public const string HomeDocumentsKey = "home.documents";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>A <see cref="ListKinds"/> value.</summary>
    public string Kind { get; set; } = ListKinds.List;

    public bool AllowFolders { get; set; } = true;

    /// <summary>Content types allowed in the list as a JSON array of ids; the first is the default.</summary>
    public string ContentTypeIds { get; set; } = "[]";

    /// <summary>A <see cref="ListVersionings"/> value.</summary>
    public string Versioning { get; set; } = ListVersionings.Off;

    /// <summary>Key of the list template the list was created from, if any (LST-16).</summary>
    public string? TemplateKey { get; set; }

    /// <summary>
    /// The list has its own permissions instead of the workspace roles' (IAM-07). Either way the list is a
    /// permission scope with <see cref="AclEntry"/> rows.
    /// </summary>
    public bool HasUniquePermissions { get; set; }

    /// <summary>Marks lists created by the system (e.g. <see cref="HomeInboxKey"/>); they cannot be deleted.</summary>
    public string? SystemKey { get; set; }

    /// <summary>Versions kept per item; older ones are removed.</summary>
    public int MaxVersions { get; set; } = DefaultMaxVersions;

    /// <summary>Where each indexed field of the list is kept (ADR-0035), as a JSON array of <see cref="IndexedField"/>.</summary>
    public string IndexedFields { get; set; } = "[]";

    /// <summary>The next free value-table field number of the list.</summary>
    public short NextValueField { get; set; } = IndexedField.FirstCustomValueField;

    /// <summary>Some indexed fields are not filled yet for the existing items (the backfill job fills them).</summary>
    public bool IndexPending { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>An item (or folder) in a list. Field values live in one JSON document.</summary>
public class ListItem : ITenantOwned, IAuditable, ISoftDeletable, IVersioned
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
    /// list id when permissions come from the list. Never empty: queries trim items with one filter on it.
    /// </summary>
    public Guid ScopeId { get; set; }

    /// <summary>The built-in <c>title</c> field, kept as a column for display, sorting and search.</summary>
    public string Title { get; set; } = "";

    /// <summary>All other field values as a JSON object (normalized by their field types).</summary>
    public string Fields { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }

    // Indexed single values (ADR-0035): which field uses which column is in ListDefinition.IndexedFields. Text columns
    // hold text, choice and boolean values, number columns numbers, date columns dates and times as stored.
    public string? Text1 { get; set; }

    public string? Text2 { get; set; }

    public string? Text3 { get; set; }

    public string? Text4 { get; set; }

    public string? Text5 { get; set; }

    public string? Text6 { get; set; }

    public string? Text7 { get; set; }

    public string? Text8 { get; set; }

    public string? Text9 { get; set; }

    public string? Text10 { get; set; }

    public double? Number1 { get; set; }

    public double? Number2 { get; set; }

    public double? Number3 { get; set; }

    public double? Number4 { get; set; }

    public double? Number5 { get; set; }

    public double? Number6 { get; set; }

    public double? Number7 { get; set; }

    public double? Number8 { get; set; }

    public double? Number9 { get; set; }

    public double? Number10 { get; set; }

    public string? Date1 { get; set; }

    public string? Date2 { get; set; }

    public string? Date3 { get; set; }

    public string? Date4 { get; set; }

    public string? Date5 { get; set; }

    public string? Date6 { get; set; }

    public string? Date7 { get; set; }

    public string? Date8 { get; set; }

    public string? Date9 { get; set; }

    public string? Date10 { get; set; }
}

/// <summary>A saved version of an item (LST-11): its values after one change, kept while the list has versioning on.</summary>
public class ItemVersion : ITenantOwned, INotAudited
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public Guid ListId { get; set; }

    public int Number { get; set; }

    public Guid ContentTypeId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Field values (without <c>title</c>) as JSON, like <see cref="ListItem.Fields"/>.</summary>
    public string Fields { get; set; } = "{}";

    /// <summary>Names of the fields changed compared with the previous version (all for the first), as a JSON array.</summary>
    public string ChangedFields { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}

/// <summary>Values of <see cref="IndexedField.Kind"/>: how an indexed field is kept.</summary>
public static class IndexKinds
{
    public const string Text = "Text";
    public const string Number = "Number";
    public const string Date = "Date";

    /// <summary>Multiple values and references (people, lookups, terms): rows in <c>item_values</c>.</summary>
    public const string Values = "Values";
}

/// <summary>Where one indexed field of a list is kept: an item column, or a field number in the value table.</summary>
public sealed record IndexedField
{
    /// <summary>Value-table field numbers below this are for the well-known fields.</summary>
    public const short FirstCustomValueField = 16;

    public required string Field { get; init; }

    /// <summary>An <see cref="IndexKinds"/> value.</summary>
    public required string Kind { get; init; }

    /// <summary>The item column (<c>Text3</c>, <c>Number1</c>, <c>Date2</c>) for single values.</summary>
    public string? Column { get; init; }

    /// <summary>The field number in <c>item_values</c> for <see cref="IndexKinds.Values"/>.</summary>
    public short? ValueField { get; init; }

    /// <summary>Filled for every item: queries use it (until then they read the JSON).</summary>
    public bool Ready { get; init; }
}

/// <summary>One value of an indexed multi-value or reference field of an item (ADR-0035): a GUID, or a name-based id of a text value.</summary>
public class ItemValue : ITenantOwned, INotAudited
{
    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public short Field { get; set; }

    public Guid Value { get; set; }
}

/// <summary>Values of <see cref="ListView.Layout"/>.</summary>
public static class ViewLayouts
{
    public const string Table = "table";
    public const string Board = "board";
    public const string Calendar = "calendar";
    public const string Gallery = "gallery";

    public static bool IsValid(string? layout) => layout is Table or Board or Calendar or Gallery;
}

/// <summary>A saved way to look at a list (LST-09): columns, filter, order, grouping and layout.</summary>
public class ListView : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Field names shown, as a JSON array (empty: all).</summary>
    public string Columns { get; set; } = "[]";

    /// <summary>OData <c>$filter</c> expression.</summary>
    public string? Filter { get; set; }

    /// <summary>OData <c>$orderby</c> expression.</summary>
    public string? OrderBy { get; set; }

    /// <summary>Field to group by (board columns, calendar date, …).</summary>
    public string? GroupBy { get; set; }

    /// <summary>A <see cref="ViewLayouts"/> value.</summary>
    public string Layout { get; set; } = ViewLayouts.Table;

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>
/// A smart folder (TAX-08): a saved, rule-based view over items of many lists. Personal folders have an
/// <see cref="OwnerId"/>; shared ones belong to <see cref="WorkspaceId"/>.
/// </summary>
public class SmartFolder : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public Guid? WorkspaceId { get; set; }

    public Guid? OwnerId { get; set; }

    /// <summary>The definition as JSON (<c>SmartFolderDefinition</c>).</summary>
    public string Definition { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>Values of <see cref="ItemChange.Kind"/>.</summary>
public static class ItemChangeKinds
{
    /// <summary>The item was added, changed, moved or restored.</summary>
    public const string Upserted = "upserted";

    /// <summary>The item was moved to the recycle bin or purged.</summary>
    public const string Deleted = "deleted";

    /// <summary>
    /// The access list of <see cref="ItemChange.ScopeId"/> changed: delta returns the scope's items again, as changed or
    /// removed for the caller (ADR-0035).
    /// </summary>
    public const string ScopeChanged = "scopeChanged";
}

/// <summary>
/// One entry of a list's change log (API-05), written in the same transaction as the change. The sequence orders
/// changes; delta tokens point into it.
/// </summary>
public class ItemChange : ITenantOwned, INotAudited
{
    public long Sequence { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>Null for <see cref="ItemChangeKinds.ScopeChanged"/>.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>The item's permission scope after the change (checks access after a purge), or the changed scope.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>
    /// The item's scope before the change when it moved to another scope: callers who could read that scope get the
    /// item as removed when they cannot read the new one.
    /// </summary>
    public Guid? FromScopeId { get; set; }

    /// <summary>An <see cref="ItemChangeKinds"/> value.</summary>
    public string Kind { get; set; } = ItemChangeKinds.Upserted;

    /// <summary>When, in Unix milliseconds (UTC): SQLite compares numbers in SQL, not <see cref="DateTimeOffset"/> (ADR-0039).</summary>
    public long At { get; set; }
}

/// <summary>An item's id and field values (a query projection, for the index backfill).</summary>
public sealed record ItemFields(Guid Id, string Fields);

/// <summary>A deleted item's id and time of deletion (a query projection).</summary>
public sealed record DeletedItem(Guid Id, DateTimeOffset? DeletedAt);

/// <summary>Values of <see cref="AclEntry.PrincipalType"/>: who a permission entry gives access to.</summary>
public static class AclPrincipalTypes
{
    public const string User = "user";
    public const string Group = "group";

    /// <summary>Visitors of the list's workspace (a role principal, see <c>WorkspaceRolePrincipals</c>).</summary>
    public const string WorkspaceVisitors = "workspaceVisitors";

    /// <summary>Members of the list's workspace.</summary>
    public const string WorkspaceMembers = "workspaceMembers";

    /// <summary>Owners of the list's workspace. Every scope has this entry with Manage (full control).</summary>
    public const string WorkspaceOwners = "workspaceOwners";

    public static bool IsValid(string? type) => type is User or Group or WorkspaceVisitors or WorkspaceMembers or WorkspaceOwners;
}

/// <summary>
/// One entry of a permission scope's access list (IAM-07, ADR-0035): a principal and its level on the items of the
/// scope. The scope is a list (<see cref="ListDefinition.Id"/>) or an item with unique permissions. Inheriting lists
/// have entries for the three workspace roles, so workspace membership changes write nothing here.
/// </summary>
public class AclEntry : ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ScopeId { get; set; }

    /// <summary>A user, a group, or a role principal (<c>WorkspaceRolePrincipals.Id</c>).</summary>
    public Guid PrincipalId { get; set; }

    /// <summary>An <see cref="AclPrincipalTypes"/> value.</summary>
    public string PrincipalType { get; set; } = "";

    /// <summary>The <c>WorkspaceAccessLevel</c> as a number (Read 1, Contribute 2, Manage 3), so queries compare levels.</summary>
    public int Level { get; set; }

    public Guid ListId { get; set; }

    public Guid WorkspaceId { get; set; }
}

/// <summary>A scope and the highest level of the caller's principals in it (a query projection).</summary>
public sealed record ScopeLevel(Guid ScopeId, int Level);

/// <summary>A list's id and workspace (a query projection).</summary>
public sealed record ListRef(Guid Id, Guid WorkspaceId);

#pragma warning restore CA1852
