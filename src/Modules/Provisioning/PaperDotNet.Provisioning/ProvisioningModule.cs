using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
/// Provisioning templates (ADR-0017): runs the template sections that modules and extensions contribute
/// (<see cref="Contracts.ITemplateHandler"/>, <see cref="Contracts.ITemplateContainer"/>). Packages with content come with T13c.
/// </summary>
public sealed class ProvisioningModule : IModule
{
    public string Name => "Provisioning";

    public IJsonTypeInfoResolver Json => ProvisioningJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped(sp => new TemplateEngine(sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddScopes(ProvisioningScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => ProvisioningEndpoints.Map(endpoints);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TemplateResult))]
internal sealed partial class ProvisioningJson : JsonSerializerContext;
