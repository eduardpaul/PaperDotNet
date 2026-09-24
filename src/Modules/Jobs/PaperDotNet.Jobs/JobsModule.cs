using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Jobs.Data;
using PaperDotNet.Jobs.Features;
using PaperDotNet.Persistence;

namespace PaperDotNet.Jobs;

public sealed class JobsModule : IModule
{
    public string Name => "Jobs";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobsOptions>().BindConfiguration(JobsOptions.Section);
        services.AddModuleDbContext<JobsDbContext>(JobsDbContext.Schema);
        services.AddScoped<IOperations, OperationService>();
        services.AddScoped<OperationRunner>();
        services.AddSingleton<OperationDispatcher>();
        services.AddHostedService<RecurringJobScheduler>();
        services.AddTenantRecurringJob<OperationsCleanupJob>(OperationsCleanupJob.Name, OperationsCleanupJob.Schedule);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => OperationEndpoints.Map(endpoints);
}
