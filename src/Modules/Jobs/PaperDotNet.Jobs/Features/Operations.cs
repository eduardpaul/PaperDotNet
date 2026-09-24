using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Jobs.Data;
using PaperDotNet.Messaging;

namespace PaperDotNet.Jobs.Features;

/// <summary>Background message that executes one operation.</summary>
public sealed record RunOperation(Guid OperationId, Guid TenantId, string TenantIdentifier, Guid? UserId) : ITenantMessage;

/// <summary>Wolverine handler for <see cref="RunOperation"/> (discovered by convention).</summary>
public static class RunOperationHandler
{
    public static Task Handle(RunOperation message, OperationDispatcher dispatcher, CancellationToken cancellationToken) =>
        dispatcher.RunAsync(message, cancellationToken);
}

/// <summary>Runs an operation message inside its tenant, as the user who started it.</summary>
public sealed class OperationDispatcher(ITenantScopeFactory tenantScopes)
{
    public async Task RunAsync(RunOperation message, CancellationToken cancellationToken)
    {
        await using var scope = tenantScopes.CreateScope(message.TenantId, message.TenantIdentifier, message.UserId);
        await scope.ServiceProvider.GetRequiredService<OperationRunner>().RunAsync(message.OperationId, cancellationToken);
    }
}

internal sealed class OperationService(JobsDbContext db, IOutbox outbox, ITenantContext tenant, ICurrentUser user) : IOperations
{
    public async Task<Guid> StartAsync<TPayload>(string type, TPayload payload, CancellationToken cancellationToken)
    {
        var operation = new Operation
        {
            Id = Ids.New(),
            Type = type,
            Status = OperationStatus.NotStarted,
            Payload = JsonSerializer.Serialize(payload, JobsJson.Options),
        };
        db.Operations.Add(operation);
        await outbox.SaveChangesAsync(
            db,
            [],
            [new RunOperation(operation.Id, tenant.TenantId!.Value, tenant.TenantIdentifier!, user.UserId)],
            cancellationToken);
        return operation.Id;
    }
}

/// <summary>Runs an operation: marks it running, calls its handler, stores the result or error.</summary>
internal sealed partial class OperationRunner(
    JobsDbContext db, IEnumerable<IOperationHandler> handlers, TimeProvider time, ILogger<OperationRunner> logger)
{
    public async Task RunAsync(Guid operationId, CancellationToken ct)
    {
        var operation = await db.Operations.FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (operation is null || operation.Status is OperationStatus.Succeeded or OperationStatus.Failed)
        {
            return; // Unknown or already finished: redelivery is harmless.
        }

        operation.Status = OperationStatus.Running;
        operation.StartedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var handler = handlers.FirstOrDefault(h => h.Type == operation.Type);
        try
        {
            if (handler is null)
            {
                throw new InvalidOperationException($"No handler for operation type '{operation.Type}'.");
            }

            var payload = JsonSerializer.Deserialize(operation.Payload, handler.PayloadType, JobsJson.Options)!;
            var result = await handler.ExecuteAsync(payload, new Progress(db, operation.Id, time), ct);
            await CompleteAsync(operationId, OperationStatus.Succeeded, result is null ? null : JsonSerializer.Serialize(result, result.GetType(), JobsJson.Options), null, ct);
        }
#pragma warning disable CA1031 // Any handler failure is recorded on the operation instead of retrying it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogOperationFailed(ex, operation.Type, operationId);
            await CompleteAsync(operationId, OperationStatus.Failed, null, ex.Message, ct);
        }
    }

    private async Task CompleteAsync(Guid operationId, OperationStatus status, string? result, string? error, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var operation = await db.Operations.FirstAsync(o => o.Id == operationId, ct);
        operation.Status = status;
        operation.Result = result;
        operation.Error = error;
        operation.CompletedAt = time.GetUtcNow();
        if (status == OperationStatus.Succeeded)
        {
            operation.PercentComplete = 100;
        }

        await db.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Operation {Type} {OperationId} failed.")]
    private partial void LogOperationFailed(Exception exception, string type, Guid operationId);

    private sealed class Progress(JobsDbContext db, Guid operationId, TimeProvider time) : IOperationProgress
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
        private DateTimeOffset _lastWrite;

        public async Task ReportAsync(int percentComplete, CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();
            if (now - _lastWrite < MinInterval && percentComplete < 100)
            {
                return;
            }

            _lastWrite = now;
            var value = Math.Clamp(percentComplete, 0, 99);
            await db.Operations.Where(o => o.Id == operationId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.PercentComplete, value), cancellationToken);
        }
    }
}

public sealed record OperationResponse(
    Guid Id,
    string Type,
    OperationStatus Status,
    int PercentComplete,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    JsonElement? Result,
    string? Error);

internal static class OperationEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapV1Group("operations", "Operations")
            .MapGet("/{id:guid}", GetAsync)
            .WithName("GetOperation");

    /// <summary>Status of an operation started by the caller.</summary>
    private static async Task<Results<Ok<OperationResponse>, ProblemHttpResult>> GetAsync(Guid id, JobsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var operation = await db.Operations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && o.CreatedBy == user.UserId, ct);
        if (operation is null)
        {
            return ApiErrors.NotFound();
        }

        return TypedResults.Ok(new OperationResponse(
            operation.Id,
            operation.Type,
            operation.Status,
            operation.PercentComplete,
            operation.CreatedAt,
            operation.StartedAt,
            operation.CompletedAt,
            operation.Result is null ? null : JsonDocument.Parse(operation.Result).RootElement.Clone(),
            operation.Error));
    }
}

internal static class JobsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
