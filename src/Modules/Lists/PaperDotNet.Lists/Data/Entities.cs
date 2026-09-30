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
}

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
