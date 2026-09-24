using PaperDotNet.Abstractions;

namespace PaperDotNet.Workspaces.Data;

/// <summary>A space for a team or project: members, and later lists and libraries.</summary>
public sealed class Workspace : ITenantOwned, IAuditable, ISoftDeletable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Set for a user's personal workspace (Home); only that user is a member.</summary>
    public Guid? PersonalOwnerId { get; set; }

    public List<WorkspaceMember> Members { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public uint Version { get; set; }
}

public enum WorkspaceRole
{
    /// <summary>Creates and edits content.</summary>
    Member = 0,

    /// <summary>Full control.</summary>
    Owner = 1,

    /// <summary>Reads only.</summary>
    Visitor = 2,
}

public sealed class WorkspaceMember : ITenantOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public WorkspaceRole Role { get; set; }
}
