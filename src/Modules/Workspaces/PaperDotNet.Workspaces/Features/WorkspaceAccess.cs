using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Workspaces.Features;

/// <summary>
/// Workspace-level access: members see their workspaces, owners change them,
/// and holders of <c>workspace.manage</c> administer all of them. Invisible
/// workspaces return 404, never 403, so their existence isn't leaked.
/// </summary>
internal sealed class WorkspaceAccess(ICurrentUser user, IEffectiveScopeProvider scopes, WorkspacesDbContext db)
{
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
