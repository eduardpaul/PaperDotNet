using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
/// Provisioning templates (ADR-0017): runs the template sections that modules and extensions contribute
/// (<see cref="Contracts.ITemplateHandler"/>, <see cref="Contracts.ITemplateContainer"/>). Packages with content and their export and import operations (PLT-13).
/// </summary>
public sealed class ProvisioningModule : IModule
{
    public string Name => "Provisioning";

    public IJsonTypeInfoResolver Json => ProvisioningJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped(sp => new TemplateEngine(sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddScopes(ProvisioningScopes.All);

        // Export and import with content (PLT-13).
        services.AddModuleDbContext<ProvisioningDbContext>();
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

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TemplateResult))]
[JsonSerializable(typeof(ExportRequest))]
[JsonSerializable(typeof(ExportResponse))]
[JsonSerializable(typeof(List<ExportResponse>))]
[JsonSerializable(typeof(ImportResponse))]
[JsonSerializable(typeof(ExportPayload))]
[JsonSerializable(typeof(ImportPayload))]
[JsonSerializable(typeof(ExportResult))]
[JsonSerializable(typeof(ImportErrors))]
internal sealed partial class ProvisioningJson : JsonSerializerContext;
