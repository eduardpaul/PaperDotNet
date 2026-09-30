using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Workspaces.Contracts;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Workspaces.Features;

/// <summary>
/// Workspace-level access: members see their workspaces, owners change them, and holders of <c>workspace.manage</c>
/// administer all of them. Invisible workspaces return 404, never 403, so their existence isn't leaked.
/// </summary>
internal sealed class WorkspaceAccess(IEffectiveScopeProvider scopes, WorkspacesDbContext db) : IWorkspaceAccess
{
    public static WorkspaceAccessLevel LevelOf(string role) => role switch
    {
        WorkspaceRoles.Owner => WorkspaceAccessLevel.Manage,
        WorkspaceRoles.Member => WorkspaceAccessLevel.Contribute,
        _ => WorkspaceAccessLevel.Read,
    };

    public async Task<bool> IsAdministratorAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        (await scopes.GetScopesAsync(tenantId, userId, cancellationToken))?.Contains(WorkspaceScopes.Manage) == true;

    public Task<Workspace?> FindAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var id = workspaceId;
        var ct = cancellationToken;
        return context.Workspaces.Where(w => w.TenantId == tenant && w.Id == id && w.DeletedAt == null).FirstOrDefaultAsync(ct);
    }

    public async Task<WorkspaceAccessLevel> GetPermissionAsync(Guid tenantId, Guid userId, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, workspaceId, cancellationToken) is null)
        {
            return WorkspaceAccessLevel.None;
        }

        if (await IsAdministratorAsync(tenantId, userId, cancellationToken))
        {
            return WorkspaceAccessLevel.Manage;
        }

        var role = await RoleAsync(tenantId, userId, workspaceId, cancellationToken);
        return role is null ? WorkspaceAccessLevel.None : LevelOf(role);
    }

    public Task<string?> RoleAsync(Guid tenantId, Guid userId, Guid workspaceId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var user = userId;
        var workspace = workspaceId;
        var ct = cancellationToken;
        return context.Members.Where(m => m.TenantId == tenant && m.WorkspaceId == workspace && m.UserId == user).Select(m => m.Role).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> CanManageAsync(Guid tenantId, Guid userId, Guid workspaceId, CancellationToken cancellationToken) =>
        await GetPermissionAsync(tenantId, userId, workspaceId, cancellationToken) == WorkspaceAccessLevel.Manage;

    public async Task<IReadOnlyList<WorkspaceMemberAccess>> GetMembersAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var workspace = workspaceId;
        var ct = cancellationToken;
        var members = await context.Members.Where(m => m.TenantId == tenant && m.WorkspaceId == workspace).ToListAsync(ct);
        return [.. members.Select(m => new WorkspaceMemberAccess(m.UserId, LevelOf(m.Role)))];
    }

    public async Task<IReadOnlyList<WorkspaceMembership>> GetMembershipsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var user = userId;
        var ct = cancellationToken;
        if (await IsAdministratorAsync(tenantId, userId, cancellationToken))
        {
            var all = await context.Workspaces.Where(w => w.TenantId == tenant && w.DeletedAt == null).Select(w => w.Id).ToListAsync(ct);
            return [.. all.Select(id => new WorkspaceMembership(id, WorkspaceAccessLevel.Manage))];
        }

        var members = await context.Members
            .Where(m => m.TenantId == tenant && m.UserId == user && context.Workspaces.Any(w => w.TenantId == tenant && w.Id == m.WorkspaceId && w.DeletedAt == null))
            .ToListAsync(ct);
        return [.. members.Select(m => new WorkspaceMembership(m.WorkspaceId, LevelOf(m.Role)))];
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        var wanted = workspaceIds.ToHashSet();
        var workspaces = await context.Workspaces.Where(w => w.TenantId == tenant && w.DeletedAt == null).ToListAsync(ct);
        return workspaces.Where(w => wanted.Contains(w.Id)).ToDictionary(w => w.Id, w => w.Name);
    }

    public async Task<Guid?> FindSharedAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var workspaceName = name;
        var ct = cancellationToken;
        var id = await context.Workspaces
            .Where(w => w.TenantId == tenant && w.DeletedAt == null && w.PersonalOwnerId == null && w.Name == workspaceName)
            .OrderBy(w => w.Id)
            .Select(w => w.Id)
            .FirstOrDefaultAsync(ct);
        return id == Guid.Empty ? null : id;
    }

    public async Task<Guid> EnsurePersonalWorkspaceAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var user = userId;
        var ct = cancellationToken;
        for (var attempt = 0; ; attempt++)
        {
            var existing = await context.Workspaces.Where(w => w.TenantId == tenant && w.PersonalOwnerId == user).Select(w => w.Id).FirstOrDefaultAsync(ct);
            if (existing != Guid.Empty)
            {
                return existing;
            }

            var workspace = new Workspace { Id = Ids.New(), TenantId = tenant, Name = "Home", Description = "Personal workspace.", PersonalOwnerId = user, CreatedBy = user };
            context.Workspaces.Add(workspace);
            context.Members.Add(new WorkspaceMember { TenantId = tenant, WorkspaceId = workspace.Id, UserId = user, Role = WorkspaceRoles.Owner });
            try
            {
                await context.SaveChangesAsync(ct);
                return workspace.Id;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Created concurrently (unique index); read it back.
                context.ChangeTracker.Clear();
            }
        }
    }
}
