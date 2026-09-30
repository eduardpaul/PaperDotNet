using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Audit.Data;
using PaperDotNet.Audit.Features;
using PaperDotNet.Persistence;

namespace PaperDotNet.Audit;

/// <summary>The audit log: <see cref="AuditSubscriber"/> records list and item events; <c>GET /v1.0/audit</c> reads them.</summary>
public sealed class AuditModule : IModule
{
    public string Name => "Audit";

    public IJsonTypeInfoResolver Json => AuditJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddModuleDbContext<AuditDbContext>().AddScopes(AuditScopes.All);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => AuditEndpoints.Map(endpoints);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AuditEntryDto))]
[JsonSerializable(typeof(Page<AuditEntryDto>))]
internal sealed partial class AuditJson : JsonSerializerContext;
