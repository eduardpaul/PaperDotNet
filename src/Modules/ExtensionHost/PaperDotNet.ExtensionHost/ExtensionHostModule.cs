using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaperDotNet.Abstractions;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.ExtensionHost.Features;
using PaperDotNet.ExtensionHost.Runtime;
using PaperDotNet.Extensions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;

namespace PaperDotNet.ExtensionHost;

public static class ExtensionScopes
{
    public const string Read = "extension.read";
    public const string Manage = "extension.manage";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "See the extensions available to the organization.", GrantedToMembers: true),
        new(Manage, "Enable, disable and configure extensions."),
    ];
}

/// <summary>
/// The extension runtime (ADR-0014): per-tenant state and the API. The extensions
/// themselves are registered by the host with <see cref="ExtensionRegistration.AddPaperDotNetExtensions"/>.
/// </summary>
public sealed class ExtensionHostModule : IModule
{
    public string Name => "ExtensionHost";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ExtensionsDbContext>(ExtensionsDbContext.Schema);
        services.AddScoped<ExtensionState>();
        services.AddScoped<IExtensionState>(sp => sp.GetRequiredService<ExtensionState>());
        services.Replace(ServiceDescriptor.Scoped<IFieldTypeAvailability>(sp => sp.GetRequiredService<ExtensionState>()));
        services.AddScopes(ExtensionScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => ExtensionEndpoints.Map(endpoints);
}
