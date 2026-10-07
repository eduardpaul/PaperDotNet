namespace PaperDotNet.Workspaces.Contracts;

/// <summary>What the current user may do in a workspace. Ordered: higher includes lower.</summary>
public enum WorkspaceAccessLevel
{
    /// <summary>Not visible: callers answer 404.</summary>
    None = 0,
    Read = 1,

    /// <summary>Create and edit content (items, documents).</summary>
    Contribute = 2,

    /// <summary>Manage the workspace and its structure (lists, views, members).</summary>
    Manage = 3,
}

/// <summary>A workspace member and the access its role gives.</summary>
public sealed record WorkspaceMemberAccess(Guid UserId, WorkspaceAccessLevel Level);

public sealed record WorkspaceMembership(Guid WorkspaceId, WorkspaceAccessLevel Level);

/// <summary>Workspace-level access for other modules (e.g. Lists).</summary>
public interface IWorkspaceAccess
{
    /// <summary>
    /// The current user's access: owners and administrators get <see cref="WorkspaceAccessLevel.Manage"/>
    /// (full control, also over content with unique permissions), members Contribute, visitors Read.
    /// </summary>
    Task<WorkspaceAccessLevel> GetPermissionAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>Members and their access (used when breaking permission inheritance).</summary>
    Task<IReadOnlyList<WorkspaceMemberAccess>> GetMembersAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>
    /// Every workspace the current user can access and the level: memberships, or
    /// all workspaces with Manage for administrators (used by search trimming).
    /// </summary>
    Task<IReadOnlyList<WorkspaceMembership>> GetMyWorkspacesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The workspaces of <paramref name="userId"/> and the level (all with Manage for administrators). Takes the user
    /// explicitly, so it also works where there is no current user (e.g. inside a cache factory).
    /// </summary>
    Task<IReadOnlyList<WorkspaceMembership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Names of workspaces by id (unknown ids are left out), e.g. to reference lists by name in templates.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken);

    /// <summary>
    /// Names of the shared (non-personal) workspaces among <paramref name="workspaceIds"/>; personal workspaces ("Home")
    /// and unknown ids are left out. Lets a client show an administrator's workspaces without everyone's Home.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetSharedNamesAsync(IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken);

    /// <summary>The shared (non-personal) workspace with this name, if any.</summary>
    Task<Guid?> FindSharedAsync(string name, CancellationToken cancellationToken);

    /// <summary>The current user's personal workspace ("Home"), created on first use.</summary>
    Task<Guid> EnsurePersonalWorkspaceAsync(CancellationToken cancellationToken);
}
