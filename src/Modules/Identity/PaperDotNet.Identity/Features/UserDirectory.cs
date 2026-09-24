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
