using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Finbuckle.MultiTenant.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Tenancy.Data;
using PaperDotNet.Tenancy.Features;
using PaperDotNet.Tenancy.Resolution;

namespace PaperDotNet.Tenancy;

public sealed class TenancyModule : IModule
{
    public string Name => "Tenancy";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TenancyOptions.Section).Get<TenancyOptions>() ?? new TenancyOptions();
        services.AddOptions<TenancyOptions>().BindConfiguration(TenancyOptions.Section).ValidateDataAnnotations().ValidateOnStart();

        services.AddModuleDbContext<TenancyDbContext>(TenancyDbContext.Schema);
        services.AddSingleton<TenantStore>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<ITenantScopeFactory, TenantScopeFactory>();
        services.AddScoped<ITenantDirectory, TenantDirectory>();

        // Resolution order: custom host → host template → header (opt-in) → token claim → default tenant.
        // The tenant guard then rejects tokens used against a different tenant.
        var builder = services.AddMultiTenant<PaperDotNetTenantInfo>()
            .WithStore(ServiceLifetime.Singleton, sp => sp.GetRequiredService<TenantStore>())
            .WithStrategy<HostMappingStrategy>(ServiceLifetime.Singleton);
        if (!string.IsNullOrWhiteSpace(options.HostTemplate))
        {
            builder.WithHostStrategy(options.HostTemplate);
        }

        if (options.AllowHeader)
        {
            builder.WithHeaderStrategy(options.HeaderName);
        }

        // Tenant from an API token's claim. Only the API token scheme can authenticate this early
        // (OAuth tokens are validated later by OpenIddict; their clients use host or header).
        builder.WithClaimStrategy(PaperDotNetClaims.TenantIdentifier, AuthenticationSchemeNames.ApiToken);
        if (!string.IsNullOrWhiteSpace(options.DefaultTenant))
        {
            // The default applies only when no tenant was asked for explicitly:
            // an unknown X-Tenant must fail, not silently fall back.
            var defaultTenant = options.DefaultTenant;
            var headerName = options.HeaderName;
            var allowHeader = options.AllowHeader;
            builder.WithHttpContextStrategy(ctx =>
                Task.FromResult(allowHeader && ctx.Request.Headers.ContainsKey(headerName) ? null : defaultTenant)!);
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => OrganizationEndpoints.Map(endpoints);
}

public static class TenancyApplicationBuilderExtensions
{
    /// <summary>Resolves the tenant. Call before authentication.</summary>
    public static IApplicationBuilder UsePaperDotNetTenantResolution(this IApplicationBuilder app) => app.UseMultiTenant();

    /// <summary>Enforces a resolved, matching tenant on API requests. Call after authentication.</summary>
    public static IApplicationBuilder UsePaperDotNetTenantGuard(this IApplicationBuilder app) => app.UseMiddleware<TenantGuardMiddleware>();
}
