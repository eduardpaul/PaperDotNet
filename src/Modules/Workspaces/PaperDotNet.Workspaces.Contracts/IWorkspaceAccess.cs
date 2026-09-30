using System.Text.Json.Serialization;

namespace PaperDotNet.Workspaces.Contracts;

/// <summary>What a user may do in a workspace. Ordered: higher includes lower. In JSON: <c>none</c>, <c>read</c>, …</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceAccessLevel>))]
public enum WorkspaceAccessLevel
{
    /// <summary>Not visible: callers answer 404.</summary>
    [JsonStringEnumMemberName("none")]
    None = 0,

    [JsonStringEnumMemberName("read")]
    Read = 1,

    /// <summary>Create and edit content (items, documents).</summary>
    [JsonStringEnumMemberName("contribute")]
    Contribute = 2,

    /// <summary>Manage the workspace and its structure (lists, views, members).</summary>
    [JsonStringEnumMemberName("manage")]
    Manage = 3,
}

/// <summary>A workspace member and the access its role gives.</summary>
public sealed record WorkspaceMemberAccess(Guid UserId, WorkspaceAccessLevel Level);

public sealed record WorkspaceMembership(Guid WorkspaceId, WorkspaceAccessLevel Level);

/// <summary>
/// Workspace-level access for other modules (e.g. Lists). Every call names its tenant and user (no ambient caller
/// under Native AOT, ADR-0039). Deleted workspaces are not visible.
/// </summary>
public interface IWorkspaceAccess
{
    /// <summary>
    /// The user's access: owners and administrators (<c>workspace.manage</c>) get <see cref="WorkspaceAccessLevel.Manage"/>
    /// (full control, also over content with unique permissions), members Contribute, visitors Read.
    /// </summary>
    Task<WorkspaceAccessLevel> GetPermissionAsync(Guid tenantId, Guid userId, Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>Members and their access (used when breaking permission inheritance).</summary>
    Task<IReadOnlyList<WorkspaceMemberAccess>> GetMembersAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>The workspaces of the user and the level: memberships, or all workspaces with Manage for administrators.</summary>
    Task<IReadOnlyList<WorkspaceMembership>> GetMembershipsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Names of workspaces by id (unknown ids are left out), e.g. to reference lists by name in templates.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken);

    /// <summary>The shared (non-personal) workspace with this name, if any.</summary>
    Task<Guid?> FindSharedAsync(Guid tenantId, string name, CancellationToken cancellationToken);

    /// <summary>The user's personal workspace ("Home"), created on first use.</summary>
    Task<Guid> EnsurePersonalWorkspaceAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}
