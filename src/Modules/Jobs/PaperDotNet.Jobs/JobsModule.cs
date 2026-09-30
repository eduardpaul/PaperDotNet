using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Jobs.Data;
using PaperDotNet.Jobs.Features;
using PaperDotNet.Persistence;

namespace PaperDotNet.Jobs;

/// <summary>
/// Long-running operations (<see cref="IOperations"/>, <c>GET /v1.0/operations/{id}</c>), recurring jobs per tenant
/// (<see cref="ITenantRecurringJob"/>, Cronos schedules) and the caller's live events (<c>GET /v1.0/me/events</c>).
/// </summary>
public sealed class JobsModule : IModule
{
    public string Name => "Jobs";

    public IJsonTypeInfoResolver Json => JobsJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JobsOptions>(configuration.GetSection(JobsOptions.Section));
        services.AddModuleDbContext<JobsDbContext>();
        services.AddScoped<IOperations, OperationService>();
        services.AddScoped<OperationRunner>();
        services.AddHostedService<RecurringJobScheduler>();
        services.AddTenantRecurringJob<OperationsCleanupJob>(OperationsCleanupJob.Name, OperationsCleanupJob.Schedule);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        OperationEndpoints.Map(endpoints);
        LiveEventsEndpoint.Map(endpoints);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OperationResponse))]
[JsonSerializable(typeof(RunOperation))]
internal sealed partial class JobsJson : JsonSerializerContext;
