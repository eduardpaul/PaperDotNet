using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

internal sealed class UserDirectory(
    IdentityDbContext db,
    UserManager<User> users,
    ITenantContext tenant,
    HybridCache cache) : IUserDirectory
{
    public Task<bool> IsActiveAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Id == userId && !u.IsDisabled, cancellationToken);

    public Task<bool> AnyUsersAsync(CancellationToken cancellationToken) => db.Users.AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetGroupIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.GroupMembers.Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync(cancellationToken);

    public Task<bool> GroupExistsAsync(Guid groupId, CancellationToken cancellationToken) =>
        db.Groups.AnyAsync(g => g.Id == groupId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        await db.Users.Where(u => userIds.Contains(u.Id) && u.UserName != null)
            .ToDictionaryAsync(u => u.Id, u => u.UserName!, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetGroupNamesAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken) =>
        await db.Groups.Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetGroupMembersAsync(Guid groupId, CancellationToken cancellationToken) =>
        await db.GroupMembers.Where(m => m.GroupId == groupId)
            .Join(db.Users.Where(u => !u.IsDisabled), m => m.UserId, u => u.Id, (m, u) => u.Id)
            .ToListAsync(cancellationToken);

    public async Task<Guid?> FindUserAsync(string userName, CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeName(userName);
        return await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid?> FindGroupAsync(string name, CancellationToken cancellationToken) =>
        await db.Groups.Where(g => g.Name == name).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid> CreateUserAsync(NewUser request, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant.");
        var user = new User
        {
            Id = Ids.New(),
            TenantId = tenantId,
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName ?? request.UserName,
        };

        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            throw new UserCreationException(result.Errors.Select(e => e.Description).ToList());
        }

        var roleNames = request.Administrator ? new[] { Role.Member, Role.Administrator } : [Role.Member];
        var roleIds = await db.Roles.Where(r => r.IsBuiltIn && roleNames.Contains(r.Name)).Select(r => r.Id).ToListAsync(cancellationToken);
        db.RoleAssignments.AddRange(roleIds.Select(roleId => new RoleAssignment
        {
            Id = Ids.New(),
            RoleId = roleId,
            PrincipalId = user.Id,
            PrincipalType = PrincipalType.User,
        }));
        await db.SaveChangesAsync(cancellationToken);
        await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(tenantId), cancellationToken);
        return user.Id;
    }
}

internal sealed class RoleProvisioning(IdentityDbContext db, ITenantContext tenant, HybridCache cache) : IRoleProvisioning
{
    public async Task GrantToMembersAsync(IReadOnlyCollection<string> scopes, CancellationToken cancellationToken)
    {
        var member = await db.Roles.FirstOrDefaultAsync(r => r.IsBuiltIn && r.Name == Role.Member, cancellationToken);
        var missing = scopes.Where(s => member is not null && !member.Scopes.Contains(s)).ToList();
        if (member is null || missing.Count == 0)
        {
            return;
        }

        member.Scopes = [.. member.Scopes, .. missing];
        await db.SaveChangesAsync(cancellationToken);
        await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(tenant.TenantId!.Value), cancellationToken);
    }
}
