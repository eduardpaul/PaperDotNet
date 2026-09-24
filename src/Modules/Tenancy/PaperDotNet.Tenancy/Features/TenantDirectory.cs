using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Tenancy.Data;
using PaperDotNet.Tenancy.Resolution;

namespace PaperDotNet.Tenancy.Features;

internal sealed partial class TenantDirectory(
    TenancyDbContext db,
    ITenantScopeFactory tenantScopes,
    HybridCache cache,
    TimeProvider time) : ITenantDirectory
{
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex IdentifierPattern();

    public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Tenants.AsNoTracking().OrderBy(t => t.Identifier).ToListAsync(cancellationToken))
            .Select(t => t.ToSummary())
            .ToList();

    public async Task<TenantSummary?> FindAsync(string identifier, CancellationToken cancellationToken) =>
        (await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Identifier == identifier, cancellationToken))?.ToSummary();

    public async Task<TenantSummary> CreateAsync(string identifier, string name, IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        identifier = identifier.Trim().ToLowerInvariant();
        if (!IdentifierPattern().IsMatch(identifier))
        {
            throw new ArgumentException("Tenant identifier must be 1-63 lowercase letters, digits or hyphens.", nameof(identifier));
        }

        if (await db.Tenants.AnyAsync(t => t.Identifier == identifier, cancellationToken))
        {
            throw new InvalidOperationException($"Tenant '{identifier}' already exists.");
        }

        var tenant = new Tenant
        {
            Id = Ids.New(),
            Identifier = identifier,
            Name = string.IsNullOrWhiteSpace(name) ? identifier : name.Trim(),
            Status = TenantStatus.Active,
            Hosts = hosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToList(),
            CreatedAt = time.GetUtcNow(),
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);

        await using (var scope = tenantScopes.CreateScope(tenant.Id, tenant.Identifier))
        {
            foreach (var initializer in scope.ServiceProvider.GetServices<ITenantInitializer>())
            {
                await initializer.InitializeAsync(tenant.Id, cancellationToken);
            }
        }

        await cache.RemoveByTagAsync(TenantStore.CacheTag, cancellationToken);
        return tenant.ToSummary();
    }

    public async Task SetStatusAsync(string identifier, TenantStatus status, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Identifier == identifier, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{identifier}' does not exist.");
        tenant.Status = status;
        await db.SaveChangesAsync(cancellationToken);
        await cache.RemoveByTagAsync(TenantStore.CacheTag, cancellationToken);
    }
}
