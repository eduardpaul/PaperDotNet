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

/// <summary>Workspace-level access for other modules (e.g. Lists).</summary>
public interface IWorkspaceAccess
{
    Task<WorkspaceAccessLevel> GetPermissionAsync(Guid workspaceId, CancellationToken cancellationToken);
}
