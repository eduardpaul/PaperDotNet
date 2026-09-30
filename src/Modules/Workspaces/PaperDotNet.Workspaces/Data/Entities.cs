using System.ComponentModel.DataAnnotations;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Workspaces.Data;

#pragma warning disable CA1852 // Not sealed: EF Core precompiled materializers (ADR-0039).

/// <summary>A space for a team or project: members, lists and libraries. Deleting it moves it to the recycle bin.</summary>
public class Workspace : ITenantOwned, IAuditable, ISoftDeletable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Set for a user's personal workspace (Home); only that user is a member.</summary>
    public Guid? PersonalOwnerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

public class WorkspaceMember : ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>A <see cref="WorkspaceRoles"/> value.</summary>
    public string Role { get; set; } = WorkspaceRoles.Member;
}

#pragma warning restore CA1852

/// <summary>Roles of workspace members, as stored and in the API.</summary>
public static class WorkspaceRoles
{
    /// <summary>Full control.</summary>
    public const string Owner = "owner";

    /// <summary>Creates and edits content.</summary>
    public const string Member = "member";

    /// <summary>Reads only.</summary>
    public const string Visitor = "visitor";

    public static bool IsValid(string? role) => role is Owner or Member or Visitor;
}
