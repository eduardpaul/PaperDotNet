using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Jobs.Contracts;

/// <summary>Status of an operation, as stored and returned (strings, not an enum: ADR-0039).</summary>
public static class OperationStatus
{
    public const string NotStarted = "notStarted";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>Starts long-running operations (EVT-06). Callers answer <c>202 Accepted</c> with the operation URL.</summary>
public interface IOperations
{
    /// <summary>
    /// Creates the operation for <paramref name="actor"/> and schedules it (atomically); returns its id. Only the user
    /// who started an operation can read it.
    /// </summary>
    Task<Guid> StartAsync(ChangeActor actor, string type, JsonNode? payload, CancellationToken cancellationToken);
}

public static class OperationsExtensions
{
    /// <summary>Starts an operation with a typed payload (serialized with source-generated JSON metadata).</summary>
    public static Task<Guid> StartAsync<TPayload>(
        this IOperations operations, ChangeActor actor, string type, TPayload payload, JsonTypeInfo<TPayload> json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations.StartAsync(actor, type, JsonSerializer.SerializeToNode(payload, json), cancellationToken);
    }
}

public interface IOperationProgress
{
    /// <summary>Reports progress (0–100). Cheap to call often; writes are throttled.</summary>
    Task ReportAsync(int percentComplete, CancellationToken cancellationToken);
}

/// <summary>What an operation handler runs for: the operation, and the tenant and user who started it.</summary>
public sealed record OperationContext(Guid OperationId, ChangeActor Actor, IOperationProgress Progress);

/// <summary>
/// Executes one operation type in the background, for the tenant and user who started it
/// (<see cref="OperationContext.Actor"/>). The returned JSON becomes the operation result. Throwing marks the
/// operation as failed.
/// </summary>
public interface IOperationHandler
{
    string Type { get; }

    Task<JsonNode?> ExecuteAsync(JsonNode? payload, OperationContext context, CancellationToken cancellationToken);
}

/// <summary>An operation handler with a typed payload (read with source-generated JSON metadata).</summary>
public abstract class OperationHandler<TPayload> : IOperationHandler
{
    public abstract string Type { get; }

    protected abstract JsonTypeInfo<TPayload> PayloadJson { get; }

    public Task<JsonNode?> ExecuteAsync(JsonNode? payload, OperationContext context, CancellationToken cancellationToken) =>
        ExecuteAsync(
            payload.Deserialize(PayloadJson) ?? throw new InvalidOperationException($"The operation '{Type}' has no payload."),
            context,
            cancellationToken);

    protected abstract Task<JsonNode?> ExecuteAsync(TPayload payload, OperationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A job that runs on a cron schedule, once per active tenant (EVT-05). Registered with
/// <c>AddTenantRecurringJob</c>. Resolved from a new scope for every tenant.
/// </summary>
public interface ITenantRecurringJob
{
    Task RunAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>A registered recurring job: name, cron schedule (5 fields, or 6 with seconds; UTC) and how to create it.</summary>
public sealed record RecurringJobRegistration(string Name, string Schedule, Func<IServiceProvider, ITenantRecurringJob> Create);

public static class JobsServiceCollectionExtensions
{
    /// <summary>Registers a recurring job that runs on <paramref name="schedule"/> (cron, UTC) for every active tenant.</summary>
    public static IServiceCollection AddTenantRecurringJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJob>(
        this IServiceCollection services, string name, string schedule)
        where TJob : class, ITenantRecurringJob
    {
        services.AddScoped<TJob>();
        services.AddSingleton(new RecurringJobRegistration(name, schedule, sp => sp.GetRequiredService<TJob>()));
        return services;
    }

    /// <summary>Registers the handler of a long-running operation type.</summary>
    public static IServiceCollection AddOperationHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services)
        where THandler : class, IOperationHandler
    {
        services.AddScoped<IOperationHandler, THandler>();
        return services;
    }
}
