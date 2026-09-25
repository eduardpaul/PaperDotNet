using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Data;
using PaperDotNet.Provisioning.Features;

namespace PaperDotNet.Provisioning;

public static class ProvisioningScopes
{
    public const string Read = "template.read";
    public const string Manage = "template.manage";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "Export the configuration of the organization or a workspace as a template."),
        new(Manage, "Apply templates: creates and changes content types, term sets, groups, roles, workspaces, lists and extension settings."),
    ];
}

/// <summary>
/// Provisioning templates (phase 5a, ADR-0017) and packages with content (PRV-04, PLT-13, ADR-0028): runs the template
/// sections that modules and extensions contribute (<see cref="Contracts.ITemplateHandler"/>). Built on the extension SDK only.
/// </summary>
public sealed class ProvisioningModule : IModule
{
    public string Name => "Provisioning";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<TemplateEngine>();
        services.AddScopes(ProvisioningScopes.All);

        // Export and import with content (PLT-13).
        services.AddModuleDbContext<ProvisioningDbContext>(ProvisioningDbContext.Schema);
        services.Configure<PortabilityOptions>(configuration.GetSection(PortabilityOptions.Section));
        services.AddScoped<PortabilityService>();
        services.AddOperationHandler<ExportOperation>();
        services.AddOperationHandler<ImportOperation>();
        services.AddTenantRecurringJob<PortabilityCleanupJob>(PortabilityCleanupJob.Name, PortabilityCleanupJob.Schedule);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ProvisioningEndpoints.Map(endpoints);
        PortabilityEndpoints.Map(endpoints);
    }
}
