using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

/// <summary>User and group lookups for other modules (Identity.Contracts), always within the named tenant.</summary>
internal sealed class UserDirectory(IdentityDbContext db, IPasswordHasher<User> hasher, TimeProvider time) : IUserDirectory
{
    public Task<bool> IsActiveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var user = userId;
        var ct = cancellationToken;
        return context.Users.AnyAsync(u => u.TenantId == tenant && u.Id == user && !u.IsDisabled && u.DeletedAt == null, ct);
    }

    public Task<bool> AnyUsersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        return context.Users.AnyAsync(u => u.TenantId == tenant && u.DeletedAt == null, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetGroupIdsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var user = userId;
        var ct = cancellationToken;
        var ancestors = await context.GroupClosures
            .Where(c => c.TenantId == tenant && context.GroupMembers.Any(m => m.TenantId == tenant && m.UserId == user && m.GroupId == c.GroupId))
            .Select(c => c.AncestorId)
            .ToListAsync(ct);
        return [.. ancestors.Distinct()];
    }

    public Task<bool> GroupExistsAsync(Guid tenantId, Guid groupId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var group = groupId;
        var ct = cancellationToken;
        return context.Groups.AnyAsync(g => g.TenantId == tenant && g.Id == group, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        var wanted = userIds.ToHashSet();
        var users = await context.Users.Where(u => u.TenantId == tenant).Select(u => new NamedPrincipal(u.Id, u.UserName)).ToListAsync(ct);
        return users.Where(u => wanted.Contains(u.Id)).ToDictionary(u => u.Id, u => u.Name);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetGroupNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        var wanted = groupIds.ToHashSet();
        var groups = await context.Groups.Where(g => g.TenantId == tenant).Select(g => new NamedPrincipal(g.Id, g.Name)).ToListAsync(ct);
        return groups.Where(g => wanted.Contains(g.Id)).ToDictionary(g => g.Id, g => g.Name);
    }

    public async Task<IReadOnlyList<Guid>> GetGroupMembersAsync(Guid tenantId, Guid groupId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var group = groupId;
        var ct = cancellationToken;
        return await context.Users
            .Where(u => u.TenantId == tenant && !u.IsDisabled && u.DeletedAt == null
                        && context.GroupMembers.Any(m => m.TenantId == tenant && m.UserId == u.Id
                                                         && context.GroupClosures.Any(c => c.TenantId == tenant && c.AncestorId == group && c.GroupId == m.GroupId)))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    public async Task<Guid?> FindUserAsync(Guid tenantId, string userName, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var normalized = Users.Normalize(userName);
        var ct = cancellationToken;
        var id = await context.Users.Where(u => u.TenantId == tenant && u.NormalizedUserName == normalized && u.DeletedAt == null).Select(u => u.Id).FirstOrDefaultAsync(ct);
        return id == Guid.Empty ? null : id;
    }

    public async Task<Guid?> FindGroupAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var groupName = name;
        var ct = cancellationToken;
        var id = await context.Groups.Where(g => g.TenantId == tenant && g.Name == groupName).Select(g => g.Id).FirstOrDefaultAsync(ct);
        return id == Guid.Empty ? null : id;
    }

    public async Task<Guid> CreateUserAsync(Guid tenantId, NewUser request, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var userName = request.UserName.Trim();
        if (userName.Length is 0 or > 256)
        {
            errors.Add("A user name of up to 256 characters is required.");
        }

        if (request.Password is not null && Users.CheckPassword(request.Password) is { } passwordError)
        {
            errors.Add(passwordError);
        }

        if (!Users.TryEmail(request.Email, out var email))
        {
            errors.Add("The e-mail field is not a valid e-mail address.");
        }

        if (request.DisplayName is { Length: > 256 })
        {
            errors.Add("The display name can have at most 256 characters.");
        }

        if (errors.Count == 0 && await Users.ExistsAsync(db, tenantId, Users.Normalize(userName), cancellationToken))
        {
            errors.Add($"The user name '{userName}' is already taken.");
        }

        if (errors.Count > 0)
        {
            throw new UserCreationException(errors);
        }

        var user = Users.New(tenantId, userName, request.DisplayName, email, time.GetUtcNow());
        if (request.Password is not null)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password);
        }

        db.Users.Add(user);
        await BuiltInRoles.AssignAsync(db, tenantId, user.Id, request.Administrator, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return user.Id;
    }
}
