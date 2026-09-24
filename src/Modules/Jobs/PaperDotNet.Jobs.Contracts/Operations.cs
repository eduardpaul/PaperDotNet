using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.Jobs.Contracts;

public enum OperationStatus
{
    NotStarted = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>Starts long-running operations (EVT-06). Callers answer <c>202 Accepted</c> with the operation URL.</summary>
public interface IOperations
{
    /// <summary>Creates the operation and schedules it (atomically); returns its id.</summary>
    Task<Guid> StartAsync<TPayload>(string type, TPayload payload, CancellationToken cancellationToken);
}

public interface IOperationProgress
{
    /// <summary>Reports progress (0–100). Cheap to call often; writes are throttled.</summary>
    Task ReportAsync(int percentComplete, CancellationToken cancellationToken);
}

/// <summary>
/// Executes one operation type. Runs in the background inside the tenant and as
/// the user who started it. The returned object becomes the operation result (JSON).
/// Throwing marks the operation as failed.
/// </summary>
public interface IOperationHandler
{
    string Type { get; }

    Type PayloadType { get; }

    Task<object?> ExecuteAsync(object payload, IOperationProgress progress, CancellationToken cancellationToken);
}

public abstract class OperationHandler<TPayload> : IOperationHandler
{
    public abstract string Type { get; }

    public Type PayloadType => typeof(TPayload);

    public Task<object?> ExecuteAsync(object payload, IOperationProgress progress, CancellationToken cancellationToken) =>
        ExecuteAsync((TPayload)payload, progress, cancellationToken);

    protected abstract Task<object?> ExecuteAsync(TPayload payload, IOperationProgress progress, CancellationToken cancellationToken);
}

/// <summary>
/// A job that runs on a cron schedule, once per active tenant, inside that tenant
/// (EVT-05). Registered with <c>AddTenantRecurringJob</c>.
/// </summary>
public interface ITenantRecurringJob
{
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>A registered recurring job: name, cron schedule (5 fields, or 6 with seconds; UTC) and implementation.</summary>
public sealed record RecurringJobRegistration(string Name, string Schedule, Type JobType);

public static class JobsServiceCollectionExtensions
{
    /// <summary>Registers a recurring job that runs on <paramref name="schedule"/> (cron, UTC) for every active tenant.</summary>
    public static IServiceCollection AddTenantRecurringJob<TJob>(
        this IServiceCollection services, string name, string schedule)
        where TJob : class, ITenantRecurringJob
    {
        services.AddScoped<TJob>();
        services.AddSingleton(new RecurringJobRegistration(name, schedule, typeof(TJob)));
        return services;
    }

    /// <summary>Registers the handler of a long-running operation type.</summary>
    public static IServiceCollection AddOperationHandler<THandler>(
        this IServiceCollection services)
        where THandler : class, IOperationHandler
    {
        services.AddScoped<IOperationHandler, THandler>();
        return services;
    }
}
