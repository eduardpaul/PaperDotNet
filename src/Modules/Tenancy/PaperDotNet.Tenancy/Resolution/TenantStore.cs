using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Tenancy.Data;

namespace PaperDotNet.Tenancy.Resolution;

/// <summary>
/// Read-only Finbuckle store over the tenants table. Only active tenants
/// resolve; suspended ones look like unknown tenants. Tenants are managed via
/// <see cref="ITenantDirectory"/>, not through this store.
/// </summary>
internal sealed class TenantStore(IServiceScopeFactory scopes, HybridCache cache) : IMultiTenantStore<PaperDotNetTenantInfo>
{
    internal const string CacheTag = "tenancy:tenants";
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromSeconds(30) };

    public Task<PaperDotNetTenantInfo?> GetByIdentifierAsync(string identifier) =>
        GetCachedAsync($"tenancy:identifier:{identifier}", t => t.Identifier == identifier);

    public Task<PaperDotNetTenantInfo?> GetAsync(string id) =>
        Guid.TryParse(id, out var guid)
            ? GetCachedAsync($"tenancy:id:{guid}", t => t.Id == guid)
            : Task.FromResult<PaperDotNetTenantInfo?>(null);

    /// <summary>Finds the tenant mapped to a custom host name.</summary>
    public Task<PaperDotNetTenantInfo?> GetByHostAsync(string host) =>
        GetCachedAsync($"tenancy:host:{host}", t => t.Hosts.Contains(host));

    public async Task<IEnumerable<PaperDotNetTenantInfo>> GetAllAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenants = await db.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active).ToListAsync();
        return tenants.Select(ToInfo);
    }

    public async Task<IEnumerable<PaperDotNetTenantInfo>> GetAllAsync(int take, int skip) =>
        (await GetAllAsync()).Skip(skip).Take(take);

    public Task<bool> AddAsync(PaperDotNetTenantInfo tenantInfo) => throw ReadOnly();

    public Task<bool> UpdateAsync(PaperDotNetTenantInfo tenantInfo) => throw ReadOnly();

    public Task<bool> RemoveAsync(string identifier) => throw ReadOnly();

    private async Task<PaperDotNetTenantInfo?> GetCachedAsync(string key, System.Linq.Expressions.Expression<Func<Tenant, bool>> predicate)
    {
        return await cache.GetOrCreateAsync(
            key,
            async ct =>
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
                var tenant = await db.Tenants.AsNoTracking()
                    .Where(t => t.Status == TenantStatus.Active)
                    .FirstOrDefaultAsync(predicate, ct);
                return tenant is null ? null : ToInfo(tenant);
            },
            CacheOptions,
            [CacheTag]);
    }

    private static PaperDotNetTenantInfo ToInfo(Tenant t) =>
        new() { Id = t.Id.ToString(), Identifier = t.Identifier, Name = t.Name };

    private static NotSupportedException ReadOnly() => new("Tenants are managed through ITenantDirectory.");
}
