using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Http;

namespace PaperDotNet.Tenancy.Resolution;

/// <summary>Resolves a tenant from a custom host name mapped to it (e.g. <c>dms.acme.com</c>).</summary>
internal sealed class HostMappingStrategy(TenantStore store) : IMultiTenantStrategy
{
    public async Task<string?> GetIdentifierAsync(object context)
    {
        if (context is not HttpContext http || string.IsNullOrEmpty(http.Request.Host.Host))
        {
            return null;
        }

        var tenant = await store.GetByHostAsync(http.Request.Host.Host.ToLowerInvariant());
        return tenant?.Identifier;
    }
}
