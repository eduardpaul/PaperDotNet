using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Jobs.Data;
using PaperDotNet.Messaging;

namespace PaperDotNet.Jobs.Features;

/// <summary>Runs one operation in the background; sent through the outbox when the operation starts.</summary>
public sealed record RunOperation(Guid TenantId, Guid OperationId);

/// <summary>Wolverine handler of <see cref="RunOperation"/> (generated ahead of time).</summary>
public static class OperationSubscriber
{
    public static Task Handle(RunOperation message, OperationRunner runner, CancellationToken cancellationToken) =>
        runner.RunAsync(message.TenantId, message.OperationId, cancellationToken);
}

internal sealed class OperationService(JobsDbContext db, IOutbox outbox, TimeProvider time) : IOperations
{
    public async Task<Guid> StartAsync(ChangeActor actor, string type, JsonNode? payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var operation = new Operation
        {
            Id = Ids.New(),
            TenantId = actor.TenantId,
            Type = type,
            Status = OperationStatus.NotStarted,
            Payload = payload?.ToJsonString(),
            CreatedBy = actor.UserId,
            CreatedAt = time.GetUtcNow(),
        };
        db.Operations.Add(operation);
        await outbox.SaveChangesAsync(db, [], [new RunOperation(operation.TenantId, operation.Id)], cancellationToken);
        return operation.Id;
    }
}

/// <summary>Runs an operation: marks it running, calls its handler, stores the result or error.</summary>
public sealed partial class OperationRunner(
    JobsDbContext db, IEnumerable<IOperationHandler> handlers, TimeProvider time, ILiveEvents live, ILogger<OperationRunner> logger)
{
    public async Task RunAsync(Guid tenantId, Guid operationId, CancellationToken cancellationToken)
    {
        var operation = await FindAsync(db, tenantId, operationId, cancellationToken);
        if (operation is null || operation.Status is OperationStatus.Succeeded or OperationStatus.Failed)
        {
            return; // Unknown or already finished: redelivery is harmless.
        }

        operation.Status = OperationStatus.Running;
        operation.StartedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        Publish(live, operation);

        var handler = handlers.FirstOrDefault(h => h.Type == operation.Type);
        try
        {
            if (handler is null)
            {
                throw new InvalidOperationException($"No handler for operation type '{operation.Type}'.");
            }

            var context = new OperationContext(
                operation.Id,
                new ChangeActor(operation.TenantId, operation.CreatedBy),
                new Progress(db, operation, time, live));
            var result = await handler.ExecuteAsync(operation.Payload is null ? null : JsonNode.Parse(operation.Payload), context, cancellationToken);
            await CompleteAsync(operation, OperationStatus.Succeeded, result?.ToJsonString(), null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Shutting down: the message is delivered again and the operation continues.
        }
#pragma warning disable CA1031 // Any handler failure is recorded on the operation instead of retrying it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogOperationFailed(ex, operation.Type, operationId);
            await CompleteAsync(operation, OperationStatus.Failed, null, ex.Message, cancellationToken);
        }
    }

    internal static Task<Operation?> FindAsync(JobsDbContext database, Guid tenantId, Guid operationId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = operationId;
        var ct = cancellationToken;
        return context.Operations.Where(o => o.TenantId == tenant && o.Id == id).FirstOrDefaultAsync(ct);
    }

    private async Task CompleteAsync(Operation operation, string status, string? result, string? error, CancellationToken cancellationToken)
    {
        operation.Status = status;
        operation.Result = result;
        operation.Error = error;
        operation.CompletedAt = time.GetUtcNow();
        if (status == OperationStatus.Succeeded)
        {
            operation.PercentComplete = 100;
        }

        await db.SaveChangesAsync(cancellationToken);
        Publish(live, operation);
    }

    /// <summary>Tells the user who started the operation about its status (API-07).</summary>
    private static void Publish(ILiveEvents live, Operation operation) =>
        live.Publish(new LiveEvent("operation", operation.TenantId, operation.CreatedBy, new JsonObject
        {
            ["id"] = operation.Id,
            ["type"] = operation.Type,
            ["status"] = operation.Status,
            ["percentComplete"] = operation.PercentComplete,
            ["error"] = operation.Error,
        }));

    [LoggerMessage(Level = LogLevel.Error, Message = "Operation {Type} {OperationId} failed.")]
    private partial void LogOperationFailed(Exception exception, string type, Guid operationId);

    private sealed class Progress(JobsDbContext db, Operation operation, TimeProvider time, ILiveEvents live) : IOperationProgress
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
            operation.PercentComplete = Math.Clamp(percentComplete, 0, 99);
            await db.SaveChangesAsync(cancellationToken);
            Publish(live, operation);
        }
    }
}

public sealed record OperationResponse(
    Guid Id,
    string Type,
    string Status,
    int PercentComplete,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    JsonNode? Result,
    string? Error);

internal static class OperationEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/v1.0/operations/{id:guid}", GetAsync).RequireAuthorization().WithTags("Operations").WithName("GetOperation")
            .WithDescription("Status of a long-running operation started by the caller (EVT-06).");

    private static async Task<Results<Ok<OperationResponse>, ProblemHttpResult>> GetAsync(Guid id, Caller caller, JobsDbContext db, CancellationToken cancellationToken)
    {
        var operation = await OperationRunner.FindAsync(db, caller.TenantId, id, cancellationToken);
        if (operation is null || operation.CreatedBy != caller.UserId)
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
            operation.Result is null ? null : JsonNode.Parse(operation.Result),
            operation.Error));
    }
}
