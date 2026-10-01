using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Bulk update body: an OData filter (optional: all items) and field values to merge.</summary>
public sealed record BulkUpdateRequest(string? Filter, JsonObject? Fields);

public sealed record BulkUpdatePayload(Guid WorkspaceId, Guid ListId, string? Filter, JsonElement Fields);

public sealed record BulkUpdateResult(int Matched, int Updated, int Failed, IReadOnlyList<BulkUpdateFailure> Failures);

public sealed record BulkUpdateFailure(Guid ItemId, string Reason);

/// <summary>An operation that was started: poll <c>/v1.0/operations/{id}</c>.</summary>
public sealed record OperationAcceptedResponse(Guid Id, string Status);

/// <summary>
/// LST-05: change fields on many items as a long-running operation (EVT-06). Every item goes through the normal
/// write path (validation, mutators, events, versions).
/// </summary>
internal static class BulkUpdateEndpoints
{
    public const string OperationType = "lists.bulkUpdate";

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGroup($"{ListEndpoints.Route}/{{listId:guid}}/items").WithTags("Items")
            .MapPost("/bulkUpdate", StartAsync)
            .RequireScope(ListScopes.Write)
            .WithName("BulkUpdateItems")
            .WithDescription("Merges fields into every item matching filter, in the background: 202 with the operation to poll.");

    private static async Task<Results<Accepted<OperationAcceptedResponse>, ValidationProblem, ProblemHttpResult>> StartAsync(
        Guid workspaceId, Guid listId, BulkUpdateRequest request, Caller caller, ListSchemaLoader loader, ItemQueryRunner runner, IOperations operations,
        CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Access.ListLevel < WorkspaceAccessLevel.Contribute)
        {
            return ListEndpoints.Forbidden();
        }

        if (request.Fields is not { Count: > 0 } fields)
        {
            return ApiErrors.Validation("fields", "A non-empty JSON object is expected.");
        }

        if (runner.Validate(schema, request.Filter, null, caller.UserId) is { } error)
        {
            return ApiErrors.Validation("filter", error);
        }

        var payload = new BulkUpdatePayload(workspaceId, listId, request.Filter, JsonSerializer.SerializeToElement(fields, ListsJson.Default.JsonObject));
        var id = await operations.StartAsync(ListEndpoints.CallerOf(caller).Actor, OperationType, payload, ListsJson.Default.BulkUpdatePayload, cancellationToken);
        return TypedResults.Accepted($"/v1.0/operations/{id}", new OperationAcceptedResponse(id, OperationStatus.NotStarted));
    }
}

/// <summary>Runs a bulk update as the user who started it: their access is checked again, item by item.</summary>
internal sealed class BulkUpdateOperation(
    ListSchemaLoader loader, IItemQueries queries, ItemQueryRunner runner, ItemWriter writer, ListsDbContext db)
    : OperationHandler<BulkUpdatePayload>
{
    private const int PageSize = 200;
    private const int MaxReportedFailures = 50;

    public override string Type => BulkUpdateEndpoints.OperationType;

    protected override JsonTypeInfo<BulkUpdatePayload> PayloadJson => ListsJson.Default.BulkUpdatePayload;

    protected override async Task<JsonNode?> ExecuteAsync(BulkUpdatePayload payload, OperationContext context, CancellationToken cancellationToken)
    {
        var caller = new ListCaller(context.Actor.TenantId, context.Actor.UserId, context.Actor.Depth);
        var schema = await loader.LoadAsync(caller, payload.WorkspaceId, payload.ListId, cancellationToken);
        if (schema is null || schema.Access.ListLevel < WorkspaceAccessLevel.Contribute)
        {
            throw new InvalidOperationException("The list is no longer accessible.");
        }

        var ids = await MatchingAsync(caller, schema, payload.Filter, cancellationToken);
        var updated = 0;
        var failed = 0;
        var failures = new List<BulkUpdateFailure>();
        for (var index = 0; index < ids.Count; index++)
        {
            if (index > 0 && index % 50 == 0)
            {
                await context.Progress.ReportAsync(index * 100 / ids.Count, cancellationToken);
            }

            var id = ids[index];
            db.ChangeTracker.Clear();
            var item = await ItemEndpoints.FindTrackedAsync(db, caller.TenantId, payload.ListId, id, cancellationToken);
            if (item is null)
            {
                continue;
            }

            var result = schema.Access.Level(item.ScopeId) < WorkspaceAccessLevel.Contribute
                ? ItemWriteResult.Denied
                : await writer.UpdateAsync(caller, schema, item, null, Optional<Guid?>.None, payload.Fields, cancellationToken);
            if (result.Item is not null)
            {
                updated++;
                continue;
            }

            failed++;
            if (failures.Count < MaxReportedFailures)
            {
                failures.Add(new BulkUpdateFailure(id, result.Cancelled
                    ?? (result.Forbidden ? "Access denied." : null)
                    ?? string.Join(" ", result.Errors?.SelectMany(e => e.Value.Select(v => $"{e.Key}: {v}")) ?? [])));
            }
        }

        await context.Progress.ReportAsync(100, cancellationToken);
        return JsonSerializer.SerializeToNode(new BulkUpdateResult(ids.Count, updated, failed, failures), ListsJson.Default.BulkUpdateResult);
    }

    /// <summary>The ids of the items (not folders) matching the filter that the caller can read, page by page.</summary>
    private async Task<List<Guid>> MatchingAsync(ListCaller caller, ListSchema schema, string? filter, CancellationToken cancellationToken)
    {
        var (parsed, error) = await runner.ParseAsync(caller.TenantId, schema, [filter], null, caller.UserId, cancellationToken);
        if (parsed is null)
        {
            throw new InvalidOperationException(error);
        }

        var ids = new List<Guid>();
        var cursor = default(ItemCursor);
        while (true)
        {
            var page = await queries.QueryAsync(
                new ItemQuery(caller.TenantId, [schema.List.Id], parsed, schema.Access.Scopes(WorkspaceAccessLevel.Read), FolderMode.ItemsOnly, null, cursor, PageSize, false, null, FieldIndex.Ready(schema.List)),
                cancellationToken);
            ids.AddRange(page.Items.Select(i => i.Id));
            if (!page.HasMore)
            {
                return ids;
            }

            cursor = new ItemCursor(page.Items[^1].Id, 0);
        }
    }
}
