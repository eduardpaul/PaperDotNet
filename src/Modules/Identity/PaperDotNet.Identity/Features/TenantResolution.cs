using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

/// <summary>Configuration section <c>Tenancy</c>: how a request names its tenant.</summary>
public sealed class TenancyOptions
{
    /// <summary>Tenant used when a request names none (single-tenant self-hosting). Clear it when hosting several.</summary>
    public string DefaultTenant { get; set; } = "default";

    /// <summary>Host names that carry the tenant, e.g. <c>{tenant}.dms.example.com</c>. Off when empty.</summary>
    public string? HostTemplate { get; set; }

    /// <summary>Allow naming the tenant with a request header (development, tests, trusted proxies).</summary>
    public bool AllowHeader { get; set; }

    public string HeaderName { get; set; } = "X-Tenant";
}

/// <summary>A tenant a request names explicitly, by host or header; <see cref="Id"/> is null when no active tenant matches.</summary>
public sealed record RequestedTenant(string Identifier, Guid? Id);

/// <summary>
/// Resolves the tenant a request names (custom host mapped to a tenant → host template → header when allowed), with
/// a short cache. Tokens carry their tenant; this decides the tenant of a sign-in without one and guards that a token
/// is not used on another tenant's host.
/// </summary>
public sealed class TenantResolver(IServiceScopeFactory scopes, IMemoryCache cache, IOptions<TenancyOptions> options)
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(30);

    private int _generation;

    public async Task<RequestedTenant?> ResolveAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var host = request.Host.Host.ToLowerInvariant();
        if (host.Length > 0 && await HostTenantAsync(host, cancellationToken) is { } mapped)
        {
            return mapped;
        }

        if (FromTemplate(settings.HostTemplate, host) is { } fromHost)
        {
            return new RequestedTenant(fromHost, await TenantIdAsync(fromHost, cancellationToken));
        }

        if (settings.AllowHeader && request.Headers[settings.HeaderName].ToString() is { Length: > 0 } header)
        {
            return new RequestedTenant(header, await TenantIdAsync(header, cancellationToken));
        }

        return null;
    }

    /// <summary>Forgets cached lookups (a tenant's hosts or status changed).</summary>
    public void Forget() => Interlocked.Increment(ref _generation);

    /// <summary>The tenant label of <paramref name="host"/> under <paramref name="template"/> (<c>{tenant}</c> stands for it).</summary>
    internal static string? FromTemplate(string? template, string host)
    {
        if (string.IsNullOrWhiteSpace(template) || host.Length == 0)
        {
            return null;
        }

        var marker = template.IndexOf("{tenant}", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var prefix = template[..marker].ToLowerInvariant();
        var suffix = template[(marker + "{tenant}".Length)..].ToLowerInvariant();
        if (host.Length <= prefix.Length + suffix.Length || !host.StartsWith(prefix, StringComparison.Ordinal) || !host.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var label = host[prefix.Length..^suffix.Length];
        return label.Contains('.', StringComparison.Ordinal) ? null : label;
    }

    /// <summary>The tenant mapped to a custom host; misses are cached too (most requests come on the server's own host).</summary>
    private Task<RequestedTenant?> HostTenantAsync(string host, CancellationToken cancellationToken) =>
        CachedAsync($"host:{host}", cacheMisses: true, async db =>
        {
            var context = db;
            var name = host;
            var active = TenantStatuses.Active;
            var ct = cancellationToken;
            var tenant = await context.Tenants.AsNoTracking()
                .Where(t => t.Status == active && context.TenantHosts.Any(h => h.Host == name && h.TenantId == t.Id))
                .FirstOrDefaultAsync(ct);
            return tenant is null ? null : new RequestedTenant(tenant.Identifier, tenant.Id);
        });

    private async Task<Guid?> TenantIdAsync(string identifier, CancellationToken cancellationToken) =>
        (await CachedAsync($"identifier:{identifier}", cacheMisses: false, async db =>
        {
            var context = db;
            var id = identifier;
            var active = TenantStatuses.Active;
            var ct = cancellationToken;
            var tenant = await context.Tenants.AsNoTracking().Where(t => t.Identifier == id && t.Status == active).FirstOrDefaultAsync(ct);
            return tenant is null ? null : new RequestedTenant(tenant.Identifier, tenant.Id);
        }))?.Id;

    /// <summary>
    /// Lookups are cached briefly. Unknown identifiers are not (a tenant created a moment ago resolves at once); unknown
    /// hosts are (host mappings change through <see cref="Forget"/>).
    /// </summary>
    private async Task<RequestedTenant?> CachedAsync(string name, bool cacheMisses, Func<IdentityDbContext, Task<RequestedTenant?>> load)
    {
        var key = $"tenancy:{Volatile.Read(ref _generation)}:{name}";
        if (cache.TryGetValue(key, out Lookup? cached))
        {
            return cached!.Tenant;
        }

        await using var scope = scopes.CreateAsyncScope();
        var tenant = await load(scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
        if (tenant is not null || cacheMisses)
        {
            cache.Set(key, new Lookup(tenant), CacheTime);
        }

        return tenant;
    }

    private sealed record Lookup(RequestedTenant? Tenant);
}

/// <summary>
/// After authentication, on API requests: a tenant named by host or header must exist and be active (404
/// <c>tenantNotFound</c>), and a token may only be used in the tenant it was issued for (403 <c>tenantMismatch</c>).
/// </summary>
internal sealed class TenantGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantResolver resolver)
    {
        if (!context.Request.Path.StartsWithSegments("/v1.0") || await resolver.ResolveAsync(context.Request, context.RequestAborted) is not { } requested)
        {
            await next(context);
            return;
        }

        if (requested.Id is not { } tenantId)
        {
            await ApiErrors.Problem(StatusCodes.Status404NotFound, "tenantNotFound", "No active tenant matches this request.").ExecuteAsync(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true && context.User.FindGuid(PaperDotNetClaims.TenantId) != tenantId)
        {
            await ApiErrors.Problem(StatusCodes.Status403Forbidden, "tenantMismatch", "The credentials were issued for a different tenant.").ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}

internal static class TenantHostQueries
{
    public static async Task<List<string>> OfTenantAsync(IdentityDbContext database, Guid tenantId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var ct = cancellationToken;
        var hosts = await db.TenantHosts.AsNoTracking().Where(h => h.TenantId == tenant).OrderBy(h => h.Host).ToListAsync(ct);
        return [.. hosts.Select(h => h.Host)];
    }
}
