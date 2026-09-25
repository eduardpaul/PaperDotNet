using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Bulk update body: an OData filter (optional: all items) and field values to merge.</summary>
public sealed record BulkUpdateRequest(string? Filter, JsonElement Fields);

public sealed record BulkUpdatePayload(Guid WorkspaceId, Guid ListId, string? Filter, JsonElement Fields);

public sealed record BulkUpdateResult(int Matched, int Updated, int Failed, IReadOnlyList<BulkUpdateFailure> Failures);

public sealed record BulkUpdateFailure(Guid ItemId, string Reason);

/// <summary>
/// LST-05: change fields on many items as a long-running operation (EVT-06).
/// Every item goes through the normal write path (validation, mutators, events).
/// </summary>
internal static class BulkUpdateEndpoints
{
    public const string OperationType = "lists.bulkUpdate";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapV1Group($"{ListEndpoints.Route}/{{listId:guid}}/items", "Items")
            .MapPost("/bulkUpdate", StartAsync)
            .RequireScope(ListScopes.Write)
            .WithName("BulkUpdateItems");

    private static async Task<Results<Accepted<OperationAcceptedResponse>, ValidationProblem, ProblemHttpResult>> StartAsync(
        Guid workspaceId, Guid listId, BulkUpdateRequest request, ListSchemaLoader loader, ItemQueryRunner runner, IOperations operations,
        CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Contribute)
        {
            return ListEndpoints.Forbidden();
        }

        if (request.Fields.ValueKind != JsonValueKind.Object || !request.Fields.EnumerateObject().Any())
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["fields"] = ["A non-empty JSON object is expected."] });
        }

        if ((await runner.FilteredAsync(schema, request.Filter, ct)).Error is { } error)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["filter"] = [error] });
        }

        var id = await operations.StartAsync(OperationType, new BulkUpdatePayload(workspaceId, listId, request.Filter, request.Fields.Clone()), ct);
        return TypedResults.Accepted($"{ApiRoutes.V1}/operations/{id}", new OperationAcceptedResponse(id, OperationStatus.NotStarted));
    }
}

public sealed record OperationAcceptedResponse(Guid Id, OperationStatus Status);

internal sealed class BulkUpdateOperation(ListSchemaLoader loader, ItemQueryRunner runner, ItemWriter writer, ListsDbContext db)
    : OperationHandler<BulkUpdatePayload>
{
    private const int BatchSize = 100;
    private const int MaxReportedFailures = 50;

    public override string Type => BulkUpdateEndpoints.OperationType;

    protected override async Task<object?> ExecuteAsync(BulkUpdatePayload payload, IOperationProgress progress, CancellationToken cancellationToken)
    {
        // Runs as the user who started it: access is checked again.
        var schema = await loader.LoadAsync(payload.WorkspaceId, payload.ListId, cancellationToken);
        if (schema is null || schema.Permission < WorkspaceAccessLevel.Contribute)
        {
            throw new InvalidOperationException("The list is no longer accessible.");
        }

        var (query, error) = await runner.FilteredAsync(schema, payload.Filter, cancellationToken);
        if (query is null)
        {
            throw new InvalidOperationException(error);
        }

        var ids = await query.OrderBy(i => i.Id).Select(i => i.Id).ToListAsync(cancellationToken);
        var updated = 0;
        var failures = new List<BulkUpdateFailure>();
        foreach (var batch in ids.Chunk(BatchSize))
        {
            foreach (var id in batch)
            {
                db.ChangeTracker.Clear();
                var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
                if (item is null)
                {
                    continue;
                }

                var result = await writer.UpdateAsync(schema, item, null, Optional<Guid?>.None, payload.Fields, cancellationToken);
                if (result.Item is not null)
                {
                    updated++;
                }
                else if (failures.Count < MaxReportedFailures)
                {
                    failures.Add(new BulkUpdateFailure(id, result.Cancelled
                        ?? (result.Forbidden ? "Access denied." : null)
                        ?? string.Join(" ", result.Errors?.SelectMany(e => e.Value.Select(v => $"{e.Key}: {v}")) ?? [])));
                }
            }

            await progress.ReportAsync(ids.Count == 0 ? 100 : (updated + failures.Count) * 100 / ids.Count, cancellationToken);
        }

        return new BulkUpdateResult(ids.Count, updated, ids.Count - updated, failures);
    }
}
