using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

/// <summary>
/// Scopes a user holds through roles assigned directly or via groups (and groups inside them). Null for unknown,
/// disabled or deleted users. Cached for a minute per tenant generation (<see cref="AccessGeneration"/>).
/// </summary>
internal sealed class EffectiveScopes(IdentityDbContext db, IScopeCatalog catalog, IMemoryCache cache) : IEffectiveScopeProvider
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlySet<string>?> GetScopesAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var key = (Kind: "scopes", tenantId, userId, AccessGeneration.Current(tenantId));
        if (cache.TryGetValue(key, out Entry? entry) && entry is not null)
        {
            return entry.Scopes;
        }

        entry = new Entry(await LoadAsync(tenantId, userId, cancellationToken));
        cache.Set(key, entry, Lifetime);
        return entry.Scopes;
    }

    private async Task<IReadOnlySet<string>?> LoadAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var user = userId;
        var ct = cancellationToken;
        if (!await context.Users.AnyAsync(u => u.TenantId == tenant && u.Id == user && !u.IsDisabled && u.DeletedAt == null, ct))
        {
            return null;
        }

        var roles = await RolesOfAsync(context, tenant, user, ct);
        return roles.Any(r => r.GrantsAllScopes)
            ? catalog.All.Select(s => s.Name).ToHashSet(StringComparer.Ordinal)
            : roles.SelectMany(r => ScopeList.Parse(r.Scopes)).Where(catalog.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Roles assigned to the user, or to a group the user is in (directly or through groups inside it).</summary>
    public static Task<List<Role>> RolesOfAsync(IdentityDbContext database, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var user = userId;
        var userType = PrincipalTypes.User;
        var groupType = PrincipalTypes.Group;
        var ct = cancellationToken;
        return context.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenant && context.RoleAssignments.Any(a => a.TenantId == tenant && a.RoleId == r.Id
                && ((a.PrincipalType == userType && a.PrincipalId == user)
                    || (a.PrincipalType == groupType && context.GroupClosures.Any(c => c.TenantId == tenant && c.AncestorId == a.PrincipalId
                        && context.GroupMembers.Any(m => m.TenantId == tenant && m.UserId == user && m.GroupId == c.GroupId))))))
            .ToListAsync(ct);
    }

    private sealed record Entry(IReadOnlySet<string>? Scopes);
}

/// <summary>The built-in Administrator and Member roles of every tenant.</summary>
internal static class BuiltInRoles
{
    /// <summary>Adds the built-in roles of a new tenant (saved by the caller); returns (administrator, member).</summary>
    public static (Role Administrator, Role Member) Add(IdentityDbContext db, Guid tenantId, IScopeCatalog catalog, DateTimeOffset now)
    {
        var administrator = new Role
        {
            Id = Ids.New(),
            TenantId = tenantId,
            Name = Role.Administrator,
            Description = "Full access to the organization.",
            IsBuiltIn = true,
            GrantsAllScopes = true,
            CreatedAt = now,
        };
        var member = new Role
        {
            Id = Ids.New(),
            TenantId = tenantId,
            Name = Role.Member,
            Description = "Everyday use: lists, documents, tasks and events.",
            IsBuiltIn = true,
            Scopes = ScopeList.Format(catalog.All.Where(s => s.GrantedToMembers).Select(s => s.Name)),
            CreatedAt = now,
        };
        db.Roles.AddRange(administrator, member);
        return (administrator, member);
    }

    /// <summary>Assigns the built-in Member role, and Administrator when asked, to a new user (saved by the caller).</summary>
    public static async Task AssignAsync(IdentityDbContext database, Guid tenantId, Guid userId, bool administrator, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var ct = cancellationToken;
        var roles = await context.Roles.Where(r => r.TenantId == tenant && r.IsBuiltIn).ToListAsync(ct);
        foreach (var role in roles.Where(r => r.Name == Role.Member || (administrator && r.Name == Role.Administrator)))
        {
            context.RoleAssignments.Add(new RoleAssignment
            {
                Id = Ids.New(),
                TenantId = tenantId,
                RoleId = role.Id,
                PrincipalId = userId,
                PrincipalType = PrincipalTypes.User,
            });
        }
    }

    /// <summary>
    /// Gives tenants created before roles existed their built-in roles, with Administrator for their first user.
    /// </summary>
    public static async Task EnsureAsync(IdentityDbContext database, IScopeCatalog catalog, TimeProvider time, CancellationToken cancellationToken)
    {
        var context = database;
        var ct = cancellationToken;
        var tenants = await context.Tenants.Where(t => !context.Roles.Any(r => r.TenantId == t.Id && r.IsBuiltIn)).Select(t => t.Id).ToListAsync(ct);
        foreach (var tenantId in tenants)
        {
            var (administrator, member) = Add(context, tenantId, catalog, time.GetUtcNow());
            var tenant = tenantId;
            var users = await context.Users.Where(u => u.TenantId == tenant && u.DeletedAt == null).OrderBy(u => u.Id).Select(u => u.Id).ToListAsync(ct);
            foreach (var (userId, index) in users.Select((id, i) => (id, i)))
            {
                foreach (var role in index == 0 ? [administrator, member] : new[] { member })
                {
                    context.RoleAssignments.Add(new RoleAssignment { Id = Ids.New(), TenantId = tenantId, RoleId = role.Id, PrincipalId = userId, PrincipalType = PrincipalTypes.User });
                }
            }
        }

        if (tenants.Count > 0)
        {
            await context.SaveChangesAsync(ct);
        }
    }
}

/// <summary>Adds scopes to the built-in Member role of a tenant.</summary>
internal sealed class RoleProvisioning(IdentityDbContext db) : IRoleProvisioning
{
    public async Task GrantToMembersAsync(Guid tenantId, IReadOnlyCollection<string> scopes, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var name = Role.Member;
        var ct = cancellationToken;
        var member = await context.Roles.Where(r => r.TenantId == tenant && r.IsBuiltIn && r.Name == name).FirstOrDefaultAsync(ct);
        var current = member is null ? [] : ScopeList.Parse(member.Scopes);
        if (member is null || scopes.All(current.Contains))
        {
            return;
        }

        member.Scopes = ScopeList.Format([.. current, .. scopes]);
        await context.SaveChangesAsync(ct);
    }
}

/// <summary>Checks that enabled administrators remain after a change (IAM-14).</summary>
internal static class AdministratorGuard
{
    /// <summary>
    /// True when at least one enabled user still holds a role that grants every scope once the given user,
    /// assignment, group, membership or nesting is gone (roles of a group reach the groups inside it).
    /// </summary>
    public static async Task<bool> RemainsAsync(
        IdentityDbContext database,
        Guid tenantId,
        Guid? withoutUser = null,
        Guid? withoutAssignment = null,
        Guid? withoutGroup = null,
        (Guid Group, Guid User)? withoutMembership = null,
        (Guid Group, Guid MemberGroup)? withoutNesting = null,
        CancellationToken cancellationToken = default)
    {
        var context = database;
        var tenant = tenantId;
        var ct = cancellationToken;
        var assignments = (await context.RoleAssignments.AsNoTracking()
                .Where(a => a.TenantId == tenant && context.Roles.Any(r => r.TenantId == tenant && r.Id == a.RoleId && r.GrantsAllScopes))
                .ToListAsync(ct))
            .Where(a => a.Id != withoutAssignment && !(a.PrincipalType == PrincipalTypes.Group && a.PrincipalId == withoutGroup))
            .ToList();

        // Administrator groups and every group inside them, without the removed group or nesting.
        var nestings = (await context.GroupNestings.AsNoTracking().Where(n => n.TenantId == tenant).Select(n => new NestingPair(n.GroupId, n.MemberGroupId)).ToListAsync(ct))
            .Where(n => n.GroupId != withoutGroup && n.MemberGroupId != withoutGroup
                        && (withoutNesting is not { } cut || n.GroupId != cut.Group || n.MemberGroupId != cut.MemberGroup))
            .ToLookup(n => n.GroupId, n => n.MemberGroupId);
        var groupIds = new HashSet<Guid>();
        var pending = new Stack<Guid>(assignments.Where(a => a.PrincipalType == PrincipalTypes.Group).Select(a => a.PrincipalId));
        while (pending.TryPop(out var group))
        {
            if (groupIds.Add(group))
            {
                foreach (var member in nestings[group])
                {
                    pending.Push(member);
                }
            }
        }

        var userIds = assignments.Where(a => a.PrincipalType == PrincipalTypes.User).Select(a => a.PrincipalId).ToHashSet();
        var members = await context.GroupMembers.AsNoTracking().Where(m => m.TenantId == tenant).ToListAsync(ct);
        userIds.UnionWith(members
            .Where(m => groupIds.Contains(m.GroupId) && (withoutMembership is not { } gone || m.GroupId != gone.Group || m.UserId != gone.User))
            .Select(m => m.UserId));
        if (withoutUser is { } removed)
        {
            userIds.Remove(removed);
        }

        var enabled = await context.Users.Where(u => u.TenantId == tenant && !u.IsDisabled && u.DeletedAt == null).Select(u => u.Id).ToListAsync(ct);
        return enabled.Any(userIds.Contains);
    }
}
