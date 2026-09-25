using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
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
/// Provisioning templates (phase 5a, ADR-0017): runs the template sections that modules and extensions
/// contribute (<see cref="Contracts.ITemplateHandler"/>). Built on the extension SDK only.
/// </summary>
public sealed class ProvisioningModule : IModule
{
    public string Name => "Provisioning";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<TemplateEngine>();
        services.AddScopes(ProvisioningScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => ProvisioningEndpoints.Map(endpoints);
}
