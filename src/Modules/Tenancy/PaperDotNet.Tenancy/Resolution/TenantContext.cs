using Finbuckle.MultiTenant.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Tenancy.Resolution;

/// <summary>
/// The current tenant: explicitly set for background work, otherwise the one
/// Finbuckle resolved for the request.
/// </summary>
internal sealed class TenantContext(IMultiTenantContextAccessor<PaperDotNetTenantInfo> accessor) : ITenantContext
{
    private (Guid Id, string Identifier)? _explicit;

    public Guid? TenantId => _explicit?.Id ?? accessor.MultiTenantContext?.TenantInfo?.TenantId;

    public string? TenantIdentifier => _explicit?.Identifier ?? accessor.MultiTenantContext?.TenantInfo?.Identifier;

    public void Set(Guid tenantId, string identifier) => _explicit = (tenantId, identifier);
}

internal sealed class TenantScopeFactory(IServiceScopeFactory scopes) : ITenantScopeFactory
{
    public async Task<AsyncServiceScope?> CreateScopeAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        await using var lookup = scopes.CreateAsyncScope();
        var tenant = (await lookup.ServiceProvider.GetRequiredService<PaperDotNet.Tenancy.Contracts.ITenantDirectory>().ListAsync(cancellationToken))
            .FirstOrDefault(t => t.Id == tenantId && t.Status == PaperDotNet.Tenancy.Contracts.TenantStatus.Active);
        return tenant is null ? null : CreateScope(tenant.Id, tenant.Identifier, userId);
    }

    public AsyncServiceScope CreateScope(Guid tenantId, string tenantIdentifier, Guid? userId = null)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, tenantIdentifier);
        if (userId is { } user)
        {
            scope.ServiceProvider.GetRequiredService<ICurrentUserOverride>().ActAs(user);
        }

        return scope;
    }
}
