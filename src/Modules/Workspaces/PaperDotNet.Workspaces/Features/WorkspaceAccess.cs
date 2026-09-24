using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Workspaces.Contracts;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Workspaces.Features;

/// <summary>
/// Workspace-level access: members see their workspaces, owners change them,
/// and holders of <c>workspace.manage</c> administer all of them. Invisible
/// workspaces return 404, never 403, so their existence isn't leaked.
/// </summary>
internal sealed class WorkspaceAccess(ICurrentUser user, IEffectiveScopeProvider scopes, WorkspacesDbContext db) : IWorkspaceAccess
{
    public async Task<WorkspaceAccessLevel> GetPermissionAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, cancellationToken))
        {
            return WorkspaceAccessLevel.None;
        }

        if (await IsAdministratorAsync(cancellationToken))
        {
            return WorkspaceAccessLevel.Manage;
        }

        var userId = user.UserId;
        var role = await db.Members
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == userId)
            .Select(m => (WorkspaceRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        return role is { } r ? LevelOf(r) : WorkspaceAccessLevel.None;
    }

    public async Task<IReadOnlyList<WorkspaceMemberAccess>> GetMembersAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        (await db.Members.AsNoTracking().Where(m => m.WorkspaceId == workspaceId).ToListAsync(cancellationToken))
            .Select(m => new WorkspaceMemberAccess(m.UserId, LevelOf(m.Role)))
            .ToList();

    public async Task<Guid> EnsurePersonalWorkspaceAsync(CancellationToken cancellationToken)
    {
        var userId = user.UserId ?? throw new InvalidOperationException("A signed-in user is required.");
        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.Workspaces.Where(w => w.PersonalOwnerId == userId).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(cancellationToken);
            if (existing is { } id)
            {
                return id;
            }

            var workspace = new Workspace { Id = Ids.New(), Name = "Home", Description = "Personal workspace.", PersonalOwnerId = userId };
            workspace.Members.Add(new WorkspaceMember { UserId = userId, Role = WorkspaceRole.Owner });
            db.Workspaces.Add(workspace);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return workspace.Id;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Created concurrently (unique index); read it back.
                db.ChangeTracker.Clear();
            }
        }
    }

    private static WorkspaceAccessLevel LevelOf(WorkspaceRole role) => role switch
    {
        WorkspaceRole.Owner => WorkspaceAccessLevel.Manage,
        WorkspaceRole.Member => WorkspaceAccessLevel.Contribute,
        _ => WorkspaceAccessLevel.Read,
    };

    public async Task<IQueryable<Workspace>> VisibleAsync(IQueryable<Workspace> query, CancellationToken ct)
    {
        if (await IsAdministratorAsync(ct))
        {
            return query;
        }

        var userId = user.UserId;
        return query.Where(w => w.Members.Any(m => m.UserId == userId));
    }

    public async Task<bool> CanManageAsync(Guid workspaceId, CancellationToken ct)
    {
        if (await IsAdministratorAsync(ct))
        {
            return await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct);
        }

        var userId = user.UserId;
        return await db.Members.AnyAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.Role == WorkspaceRole.Owner
                 && db.Workspaces.Any(w => w.Id == workspaceId), ct);
    }

    private async Task<bool> IsAdministratorAsync(CancellationToken ct) =>
        user.UserId is { } id && (await scopes.GetScopesAsync(id, ct))?.Contains(WorkspaceScopes.Manage) == true;
}
