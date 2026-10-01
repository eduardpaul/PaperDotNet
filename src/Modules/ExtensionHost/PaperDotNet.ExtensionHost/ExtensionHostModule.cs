using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaperDotNet.Abstractions;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.ExtensionHost.Features;
using PaperDotNet.ExtensionHost.Runtime;
using PaperDotNet.Extensions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;

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
/// The extension runtime (ADR-0014): per-tenant state, the API, and the delivery of list events to extensions. The
/// extensions themselves are registered by the host with <see cref="ExtensionRegistration.AddPaperDotNetExtensions"/>.
/// </summary>
public sealed class ExtensionHostModule : IModule
{
    public string Name => "ExtensionHost";

    public IJsonTypeInfoResolver Json => ExtensionHostJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ExtensionsDbContext>();
        services.AddScoped<ExtensionState>();
        services.AddScoped<IExtensionState>(sp => sp.GetRequiredService<ExtensionState>());
        services.Replace(ServiceDescriptor.Scoped<IFieldTypeAvailability>(sp => sp.GetRequiredService<ExtensionState>()));
        services.Replace(ServiceDescriptor.Scoped<IExtensionAvailability>(sp => sp.GetRequiredService<ExtensionState>()));
        services.AddScoped(sp => new ExtensionEvents(sp, sp.GetRequiredService<IExtensionState>(), sp.GetRequiredService<ILogger<ExtensionEvents>>()));
        services.AddScoped<ITemplateHandler>(sp => new ExtensionTemplateHandler(
            sp.GetRequiredService<ExtensionCatalog>(), sp.GetRequiredService<ExtensionsDbContext>(), sp.GetRequiredService<ExtensionState>(),
            sp.GetRequiredService<IRoleProvisioning>(), sp.GetRequiredService<IContentTypeProvisioning>(), sp.GetRequiredService<ITermSetProvisioning>()));
        services.AddScopes(ExtensionScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ExtensionEndpoints.Map(endpoints);
        endpoints.MapPaperDotNetExtensions();
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ExtensionResponse))]
[JsonSerializable(typeof(List<ExtensionResponse>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ExtensionHostJson : JsonSerializerContext;
