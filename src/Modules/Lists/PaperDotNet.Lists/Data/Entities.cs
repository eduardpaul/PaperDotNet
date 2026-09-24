using PaperDotNet.Abstractions;

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
