using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OData;
using PaperDotNet.Api;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// How many readable items have a value: the stored value as text (a choice, a number, a date, or a person, lookup
/// or term id), or null for items without one.
/// </summary>
public sealed record ValueCount(string? Value, long Count);

public sealed record ValueCountsResponse(IReadOnlyList<ValueCount> Value);

/// <summary>
/// Counts per value of a field (ADR-0035), for board columns, group-by and facets:
/// <c>GET …/items/counts?field=status&amp;$filter=…</c>. Folders are not counted; an item counts once for each of its
/// values (multi-value fields).
/// </summary>
internal static class ItemCountEndpoints
{
    public const int MaxValues = 200;

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGroup($"{ListEndpoints.Route}/{{listId:guid}}/items").WithTags("Items")
            .MapGet("/counts", CountAsync)
            .RequireScope(ListScopes.Read)
            .WithName("CountItemValues")
            .WithDescription("Counts per value of a field (at most 200 values, most frequent first), of the items matching $filter.");

    private static async Task<Results<Ok<ValueCountsResponse>, ValidationProblem, ProblemHttpResult>> CountAsync(
        Guid workspaceId, Guid listId, string field, [FromQuery(Name = "$filter")] string? filter, Caller caller, ListSchemaLoader loader,
        FieldTypeRegistry fieldTypes, IItemQueries queries, TimeProvider time, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (!schema.Fields.TryGetValue(field, out var definition))
        {
            return ApiErrors.Validation("field", $"The list has no field '{field}'.");
        }

        var (parsed, error) = ItemQueryParser.Parse(schema.Fields, fieldTypes, [filter], null, caller.UserId, time.GetUtcNow());
        if (parsed is null)
        {
            return ApiErrors.Validation("$filter", error!);
        }

        var query = new ItemQuery(
            caller.TenantId, [schema.List.Id], parsed, schema.Access.Scopes(WorkspaceAccessLevel.Read), FolderMode.ItemsOnly, null, default, MaxValues, false, null, FieldIndex.Ready(schema.List));
        try
        {
            var counts = await queries.CountValuesAsync(query, field, definition.AllowMultiple, cancellationToken);
            return TypedResults.Ok(new ValueCountsResponse([.. counts.OrderByDescending(c => c.Count).ThenBy(c => c.Value, StringComparer.Ordinal)]));
        }
        catch (Exception ex) when (ex is NotSupportedException or ODataException)
        {
            return ApiErrors.Validation("$filter", ex.Message);
        }
    }
}
