using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// How many readable items have a value: the stored value as text (a choice, a number, a date, or a person, lookup
/// or term id), or null for items without one.
/// </summary>
public sealed record ValueCount(string? Value, long Count);

public sealed record ValueCountsResponse(IReadOnlyList<ValueCount> Value);

/// <summary>
/// Counts per value of a field (ADR-0035), for board columns, group-by and facets:
/// <c>GET …/items/counts?field=status&amp;$filter=…</c>. An item counts once for each of its values. Indexed fields use
/// their column or the value table; a single-value field that is not indexed is grouped from the JSON; a multi-value
/// field must be indexed.
/// </summary>
internal static class ItemCountEndpoints
{
    private const int MaxValues = 200;

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapV1Group($"{ListEndpoints.Route}/{{listId:guid}}/items", "Items")
            .MapGet("/counts", CountAsync)
            .RequireScope(ListScopes.Read)
            .WithName("CountItemValues");

    private static async Task<Results<Ok<ValueCountsResponse>, ValidationProblem, ProblemHttpResult>> CountAsync(
        Guid workspaceId, Guid listId, string field, HttpRequest http, ListSchemaLoader loader, ItemQueryRunner runner, ListsDbContext db,
        CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (!schema.Fields.TryGetValue(field, out var definition))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["field"] = [$"The list has no field '{field}'."] });
        }

        var filter = http.Query.TryGetValue("$filter", out var f) && !string.IsNullOrWhiteSpace(f) ? f.ToString() : null;
        var (items, error) = await runner.MatchingAsync(schema, filter, i => !i.IsFolder, ct);
        if (items is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["$filter"] = [error!] });
        }

        var indexed = FieldIndex.Ready(schema.List, field);
        List<ValueCount> counts;
        if (indexed is { Kind: IndexKind.Values, ValueField: { } number })
        {
            var ids = items.Select(i => i.Id);
            var grouped = await db.ItemValues.AsNoTracking()
                .Where(v => v.ListId == listId && v.Field == number && ids.Contains(v.ItemId))
                .GroupBy(v => v.Value)
                .Select(g => new { g.Key, Count = g.LongCount() })
                .OrderByDescending(g => g.Count).Take(MaxValues)
                .ToListAsync(ct);
            var choices = definition.Choices.ToDictionary(c => FieldIndex.ValueId(field, c), c => c);
            counts = [.. grouped.Select(g => new ValueCount(choices.GetValueOrDefault(g.Key) ?? g.Key.ToString(), g.Count))];
            var empty = await items.LongCountAsync(i => !db.ItemValues.Any(v => v.ItemId == i.Id && v.Field == number), ct);
            if (empty > 0)
            {
                counts.Add(new ValueCount(null, empty));
            }
        }
        else if (indexed is { Column: { } column, Kind: IndexKind.Number })
        {
            counts = [.. (await items.GroupBy(i => EF.Property<double?>(i, column)).Select(g => new { g.Key, Count = g.LongCount() })
                    .OrderByDescending(g => g.Count).Take(MaxValues).ToListAsync(ct))
                .Select(g => new ValueCount(g.Key?.ToString(CultureInfo.InvariantCulture), g.Count))];
        }
        else if (indexed is { Column: { } textColumn })
        {
            counts = [.. (await items.GroupBy(i => EF.Property<string?>(i, textColumn)).Select(g => new { g.Key, Count = g.LongCount() })
                    .OrderByDescending(g => g.Count).Take(MaxValues).ToListAsync(ct))
                .Select(g => new ValueCount(g.Key, g.Count))];
        }
        else if (definition.AllowMultiple)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["field"] = [$"Field '{field}' has multiple values: index it to count them."] });
        }
        else
        {
            counts = [.. (await items.GroupBy(ItemFields.GroupKey(field, null)).Select(g => new { g.Key, Count = g.LongCount() })
                    .OrderByDescending(g => g.Count).Take(MaxValues).ToListAsync(ct))
                .Select(g => new ValueCount(g.Key, g.Count))];
        }

        return TypedResults.Ok(new ValueCountsResponse([.. counts.OrderByDescending(c => c.Count).ThenBy(c => c.Value, StringComparer.Ordinal)]));
    }
}
