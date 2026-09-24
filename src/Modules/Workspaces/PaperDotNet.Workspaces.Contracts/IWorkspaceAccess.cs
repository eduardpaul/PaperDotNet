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

    /// <summary>The current user's personal workspace ("Home"), created on first use.</summary>
    Task<Guid> EnsurePersonalWorkspaceAsync(CancellationToken cancellationToken);
}
