using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

/// <summary>
/// Scopes a user holds in the current tenant through roles assigned directly
/// or via groups. Returns null for unknown or disabled users. Cached briefly;
/// invalidated when users, groups or roles change.
/// </summary>
internal sealed class EffectiveScopeProvider(IdentityDbContext db, ITenantContext tenant, IScopeCatalog catalog, HybridCache cache)
    : IEffectiveScopeProvider
{
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromMinutes(1) };

    public static string TenantTag(Guid tenantId) => $"identity:scopes:{tenantId}";

    public async Task<IReadOnlySet<string>?> GetScopesAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return null;
        }

        var scopes = await cache.GetOrCreateAsync(
            $"identity:scopes:{tenantId}:{userId}",
            ct => new ValueTask<string[]?>(LoadAsync(userId, ct)),
            CacheOptions,
            [TenantTag(tenantId)],
            cancellationToken);
        return scopes?.ToHashSet(StringComparer.Ordinal);
    }

    private async Task<string[]?> LoadAsync(Guid userId, CancellationToken ct)
    {
        var active = await db.Users.AnyAsync(u => u.Id == userId && !u.IsDisabled, ct);
        if (!active)
        {
            return null;
        }

        var groupIds = db.GroupMembers.Where(m => m.UserId == userId).Select(m => m.GroupId);
        var roles = await db.RoleAssignments
            .Where(a => (a.PrincipalType == PrincipalType.User && a.PrincipalId == userId)
                        || (a.PrincipalType == PrincipalType.Group && groupIds.Contains(a.PrincipalId)))
            .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { r.GrantsAllScopes, r.Scopes })
            .ToListAsync(ct);

        return roles.Any(r => r.GrantsAllScopes)
            ? catalog.All.Select(s => s.Name).ToArray()
            : roles.SelectMany(r => r.Scopes).Where(catalog.Contains).Distinct().ToArray();
    }
}
