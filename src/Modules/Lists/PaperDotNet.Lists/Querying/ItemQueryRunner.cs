using System.Buffers.Text;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData;
using Microsoft.OData.UriParser;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Querying;

/// <summary>Graph-style query options for list items.</summary>
internal sealed record ItemQueryOptions(string? Filter, string? OrderBy, int Top, string? SkipToken, bool Count, IReadOnlyList<string>? Select)
{
    public const int DefaultTop = 100;
    public const int MaxTop = ListItemQuery.MaxTop;

    public static ItemQueryOptions From(HttpRequest request)
    {
        var top = DefaultTop;
        if (request.Query.TryGetValue("$top", out var topValue) && int.TryParse(topValue, CultureInfo.InvariantCulture, out var parsed))
        {
            top = Math.Clamp(parsed, 1, MaxTop);
        }

        var select = request.Query.TryGetValue("$select", out var selectValue) && !string.IsNullOrWhiteSpace(selectValue)
            ? selectValue.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
        return new ItemQueryOptions(
            NullIfEmpty(request.Query["$filter"]),
            NullIfEmpty(request.Query["$orderby"]),
            top,
            NullIfEmpty(request.Query["$skiptoken"]),
            string.Equals(request.Query["$count"], "true", StringComparison.OrdinalIgnoreCase),
            select);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>A page of items in Graph/OData shape.</summary>
public sealed record ItemPage(
    [property: JsonPropertyName("value")] IReadOnlyList<ItemResponse> Value,
    [property: JsonPropertyName("@odata.count")] long? Count,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

/// <summary>Parses, validates and runs item queries against one list.</summary>
internal sealed class ItemQueryRunner(ListsDbContext db, FieldTypeRegistry fieldTypes, ITermStore terms, ICurrentUser user, TimeProvider time)
{
    /// <summary>Checks a <c>$filter</c>/<c>$orderby</c> pair against the list schema; returns an error or null.</summary>
    public string? Validate(ListSchema schema, string? filter, string? orderBy)
    {
        try
        {
            Parse(schema, filter, orderBy, null);
            return null;
        }
        catch (ODataException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Items of the list matching an OData <c>$filter</c> (null = all), for bulk work.</summary>
    public async Task<(IQueryable<ListItem>? Query, string? Error)> FilteredAsync(ListSchema schema, string? filter, CancellationToken ct)
    {
        IQueryable<ListItem> query = db.Items.Where(i => i.ListId == schema.List.Id && !i.IsFolder);
        if (schema.Access.Filter(WorkspaceAccessLevel.Contribute) is { } writable)
        {
            query = query.Where(writable);
        }

        try
        {
            var hierarchy = await TermHierarchyAsync(schema, [filter], ct);
            var (translator, clause, _) = Parse(schema, filter, null, hierarchy);
            return (clause is null ? query : query.Where(await PreparedAsync(translator, clause, schema.List.Id, ct)), null);
        }
        catch (ODataException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Readable items (no folders) matching <paramref name="filter"/>, for code (<c>IListItemStore</c>).</summary>
    public async Task<(List<ListItem>? Items, string? Error)> ListAsync(ListSchema schema, string? filter, string? orderBy, int top, CancellationToken ct)
    {
        var (items, _, error) = await ListPageAsync(schema, filter, orderBy, top, null, null, false, ct);
        return (items, error);
    }

    /// <summary>
    /// One page of readable items. <paramref name="oneFolder"/> limits the page to the children of <paramref name="parentId"/>
    /// (null: the list root) and includes folders; otherwise folders are excluded and the parent is ignored.
    /// The cursor from <see cref="ItemCursor"/> is only valid with the same filter and order.
    /// </summary>
    public async Task<(List<ListItem>? Items, string? NextCursor, string? Error)> ListPageAsync(
        ListSchema schema, string? filter, string? orderBy, int top, string? skipToken, Guid? parentId, bool oneFolder, CancellationToken ct)
    {
        if (!ItemCursor.TryDecode(skipToken, out var cursor))
        {
            return (null, null, "The cursor is not valid. Omit it to read the first page.");
        }

        IQueryable<ListItem> query = db.Items.AsNoTracking().Where(i => i.ListId == schema.List.Id);
        if (oneFolder)
        {
            query = parentId is { } parent ? query.Where(i => i.ParentId == parent) : query.Where(i => i.ParentId == null);
        }
        else
        {
            query = query.Where(i => !i.IsFolder);
        }

        if (schema.Access.Filter(WorkspaceAccessLevel.Read) is { } readable)
        {
            query = query.Where(readable);
        }

        try
        {
            var hierarchy = await TermHierarchyAsync(schema, [filter], ct);
            var (translator, filterClause, orderByClause) = Parse(schema, filter, orderBy, hierarchy);
            if (filterClause is not null)
            {
                query = query.Where(await PreparedAsync(translator, filterClause, schema.List.Id, ct));
            }

            var customOrder = orderByClause is not null;
            if ((cursor.After is not null && customOrder) || (cursor.Offset > 0 && !customOrder))
            {
                return (null, null, "The cursor does not match this ordering. Omit it to read the first page.");
            }

            if (!customOrder)
            {
                if (cursor.After is { } after)
                {
                    query = query.Where(i => i.Id.CompareTo(after) > 0);
                }

                query = query.OrderBy(i => i.Id);
            }
            else
            {
                query = translator.OrderBy(query, orderByClause!).Skip(cursor.Offset);
            }

            var take = Math.Clamp(top, 1, ItemQueryOptions.MaxTop);
            var rows = await query.Take(take + 1).ToListAsync(ct);
            var hasMore = rows.Count > take;
            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            var next = hasMore ? customOrder ? ItemCursor.ForOffset(cursor.Offset + take) : ItemCursor.Keyset(rows[^1].Id) : null;
            return (rows, next, null);
        }
        catch (ODataException ex)
        {
            return (null, null, ex.Message);
        }
    }

    /// <summary>Readable items of the list matching <paramref name="filter"/> and <paramref name="extra"/>.</summary>
    internal Task<(IQueryable<ListItem>? Query, string? Error)> MatchingAsync(
        ListSchema schema, string? filter, Expression<Func<ListItem, bool>>? extra, CancellationToken ct) =>
        ReadableAsync(schema, filter, extra, ct);

    /// <summary>Parses a filter for callers that need the translator (smart-folder classification). No term expansion.</summary>
    internal (ItemQueryTranslator? Translator, FilterClause? Clause, string? Error) TryFilter(ListSchema schema, string? filter)
    {
        try
        {
            var (translator, clause, _) = Parse(schema, filter, null, null);
            return (translator, clause, null);
        }
        catch (ODataException ex)
        {
            return (null, null, ex.Message);
        }
    }

    private async Task<(IQueryable<ListItem>? Query, string? Error)> ReadableAsync(
        ListSchema schema, string? filter, Expression<Func<ListItem, bool>>? extra, CancellationToken ct)
    {
        IQueryable<ListItem> query = db.Items.AsNoTracking().Where(i => i.ListId == schema.List.Id);
        if (schema.Access.Filter(WorkspaceAccessLevel.Read) is { } readable)
        {
            query = query.Where(readable);
        }

        if (extra is not null)
        {
            query = query.Where(extra);
        }

        try
        {
            var hierarchy = await TermHierarchyAsync(schema, [filter], ct);
            var (translator, clause, _) = Parse(schema, filter, null, hierarchy);
            return (clause is null ? query : query.Where(await PreparedAsync(translator, clause, schema.List.Id, ct)), null);
        }
        catch (ODataException ex)
        {
            return (null, ex.Message);
        }
    }

    public async Task<(ItemPage? Page, string? Error)> RunAsync(
        ListSchema schema, ItemQueryOptions options, ListView? view, Expression<Func<ListItem, bool>>? scope, HttpRequest request, CancellationToken ct)
    {
        IQueryable<ListItem> query = db.Items.AsNoTracking().Where(i => i.ListId == schema.List.Id);
        if (schema.Access.Filter(WorkspaceAccessLevel.Read) is { } readable)
        {
            query = query.Where(readable);
        }

        if (scope is not null)
        {
            query = query.Where(scope);
        }

        ItemQueryTranslator translator;
        OrderByClause? orderBy;
        try
        {
            var hierarchy = await TermHierarchyAsync(schema, [view?.Filter, options.Filter], ct);
            // Each parse has its own parameter aliases, so each clause uses its own translator.
            var (viewTranslator, viewFilter, viewOrder) = Parse(schema, view?.Filter, view?.OrderBy, hierarchy);
            (translator, var filter, orderBy) = Parse(schema, options.Filter, options.OrderBy, hierarchy);
            if (orderBy is null && viewOrder is not null)
            {
                (orderBy, translator) = (viewOrder, viewTranslator);
            }

            if (viewFilter is not null)
            {
                query = query.Where(await PreparedAsync(viewTranslator, viewFilter, schema.List.Id, ct));
            }

            if (filter is not null)
            {
                var own = Parse(schema, options.Filter, null, hierarchy);
                query = query.Where(await PreparedAsync(own.Translator, own.Filter!, schema.List.Id, ct));
            }
        }
        catch (ODataException ex)
        {
            return (null, ex.Message);
        }

        // The total is counted on the first page only (issue 0001).
        long? count = options.Count && options.SkipToken is null ? await query.LongCountAsync(ct) : null;

        var cursor = ItemCursor.Decode(options.SkipToken);
        var sortKeys = orderBy is null ? null : ItemQueryTranslator.SortKeys(orderBy);
        IQueryable<ListItem> page;
        if (orderBy is null)
        {
            // Keyset paging on the time-ordered id.
            if (cursor.After is { } after)
            {
                query = query.Where(i => i.Id.CompareTo(after) > 0);
            }

            page = query.OrderBy(i => i.Id);
        }
        else if (sortKeys is not null)
        {
            // Folders first and titles: continue after the last row (index (ListId, ParentId, IsFolder, Title, Id)).
            if (cursor is { After: { } after, Keys: { } keys } && keys.Count == sortKeys.Count)
            {
                query = query.Where(ItemQueryTranslator.After(sortKeys, keys, after));
            }

            page = translator.OrderBy(query, orderBy);
        }
        else
        {
            page = translator.OrderBy(query, orderBy).Skip(cursor.Offset);
        }

        var items = await page.Take(options.Top + 1).ToListAsync(ct);
        var hasMore = items.Count > options.Top;
        items = items.Take(options.Top).ToList();

        string? nextLink = null;
        if (hasMore)
        {
            var last = items[^1];
            var next = orderBy is null ? ItemCursor.Keyset(last.Id)
                : sortKeys is not null ? ItemCursor.SortKeyset(ItemQueryTranslator.KeyValues(sortKeys, last), last.Id)
                : ItemCursor.ForOffset(cursor.Offset + options.Top);
            nextLink = NextLink(request, next);
        }

        var select = options.Select ?? (view?.Columns.Count > 0 ? view.Columns : null);
        return (new ItemPage(items.Select(i => ItemResponse.From(i, select)).ToList(), count, nextLink), null);
    }

    /// <summary>
    /// Descendants of the GUID literals in the filters, when the list has managed
    /// metadata fields (filtering on a term also matches its child terms).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>?> TermHierarchyAsync(ListSchema schema, string?[] filters, CancellationToken ct)
    {
        if (!schema.Fields.Values.Any(f => f.Type == ManagedMetadataFieldType.TypeName))
        {
            return null;
        }

        var ids = new HashSet<Guid>();
        foreach (var filter in filters.Where(f => f is not null))
        {
            var (_, clause, _) = Parse(schema, filter, null, null);
            if (clause is not null)
            {
                CollectGuids(clause.Expression, ids);
            }
        }

        return ids.Count == 0 ? null : await terms.GetDescendantsAsync(ids, ct);
    }

    private static void CollectGuids(QueryNode? node, HashSet<Guid> ids)
    {
        switch (node)
        {
            case ConstantNode { Value: Guid id }:
                ids.Add(id);
                break;
            case ConvertNode convert:
                CollectGuids(convert.Source, ids);
                break;
            case BinaryOperatorNode binary:
                CollectGuids(binary.Left, ids);
                CollectGuids(binary.Right, ids);
                break;
            case UnaryOperatorNode unary:
                CollectGuids(unary.Operand, ids);
                break;
            case InNode inNode when inNode.Right is CollectionConstantNode values:
                foreach (var value in values.Collection)
                {
                    CollectGuids(value, ids);
                }

                break;
            case AnyNode any:
                CollectGuids(any.Body, ids);
                break;
        }
    }

    private (ItemQueryTranslator Translator, FilterClause? Filter, OrderByClause? OrderBy) Parse(
        ListSchema schema, string? filter, string? orderBy, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? hierarchy)
    {
        var model = ItemEdmModel.Build(schema.Fields, fieldTypes);
        var options = QueryAliases.For(user.UserId, time.GetUtcNow());
        if (filter is not null)
        {
            options["$filter"] = filter;
        }

        if (orderBy is not null)
        {
            options["$orderby"] = orderBy;
        }

        var parser = new ODataQueryOptionParser(model.Model, model.ItemType, model.Items, options);
        var filterClause = parser.ParseFilter();
        var orderByClause = parser.ParseOrderBy();
        var translator = new ItemQueryTranslator(model, hierarchy, parser.ParameterAliasNodes, schema.List, db.ItemValues.AsNoTracking());

        // Translate once to surface unsupported constructs as validation errors.
        if (filterClause is not null)
        {
            translator.Filter(filterClause);
        }

        if (orderByClause is not null)
        {
            translator.OrderBy(Enumerable.Empty<ListItem>().AsQueryable(), orderByClause);
        }

        return (translator, filterClause, orderByClause);
    }

    /// <summary>Above this many matching values a value-table filter tests each item (EXISTS) instead of reading ids (IN).</summary>
    private const int SelectiveLimit = 2000;

    /// <summary>
    /// Counts (up to <see cref="SelectiveLimit"/>) the items of each value-table filter, so rare values are read by id
    /// and common ones tested per item (ADR-0035). Cheap: one index range per filter.
    /// </summary>
    private async Task<Expression<Func<ListItem, bool>>> PreparedAsync(ItemQueryTranslator translator, FilterClause clause, Guid listId, CancellationToken ct)
    {
        foreach (var (field, ids) in translator.ValueFilters(clause))
        {
            var key = ItemQueryTranslator.SelectivityKey(field, ids);
            if (translator.Selective.Contains(key))
            {
                continue;
            }

            var count = await db.ItemValues.AsNoTracking()
                .Where(v => v.ListId == listId && v.Field == field && EF.Parameter(ids).Contains(v.Value))
                .Take(SelectiveLimit + 1)
                .CountAsync(ct);
            if (count <= SelectiveLimit)
            {
                translator.Selective.Add(key);
            }
        }

        return translator.Filter(clause);
    }

    private static string NextLink(HttpRequest request, string cursor)
    {
        var query = request.Query
            .Where(q => q.Key is not ("$skiptoken" or "$count"))
            .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}")
            .Append($"$skiptoken={cursor}");
        return $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}?{string.Join('&', query)}";
    }
}

/// <summary>Opaque <c>$skiptoken</c>: keyset position (default order) or offset (custom <c>$orderby</c>).</summary>
/// <summary>
/// Opaque <c>$skiptoken</c>: keyset position (default order: the last id; folder-first and title orders: the last row's
/// sort keys and id, issue 0001) or offset (other custom <c>$orderby</c>).
/// </summary>
internal readonly record struct ItemCursor(Guid? After, int Offset, IReadOnlyList<string>? Keys = null)
{
    public static string Keyset(Guid after) => Encode("k:" + after.ToString("N"));

    public static string SortKeyset(IReadOnlyList<string> keys, Guid after) =>
        Encode("s:" + JsonSerializer.Serialize(new SortPosition([.. keys], after.ToString("N"))));

    public static string ForOffset(int offset) => Encode("o:" + offset.ToString(CultureInfo.InvariantCulture));

    public static ItemCursor Decode(string? token) => TryDecode(token, out var cursor) ? cursor : default;

    /// <summary>False when <paramref name="token"/> is present but not a cursor this server issued.</summary>
    public static bool TryDecode(string? token, out ItemCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(token))
        {
            return true;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        }
        catch (FormatException)
        {
            return false;
        }

        if (text.StartsWith("k:", StringComparison.Ordinal) && Guid.TryParseExact(text[2..], "N", out var id))
        {
            cursor = new ItemCursor(id, 0);
            return true;
        }

        if (text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0)
        {
            cursor = new ItemCursor(null, offset);
            return true;
        }

        if (text.StartsWith("s:", StringComparison.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<SortPosition>(text[2..]) is { Keys: { } keys, Id: { } last } && Guid.TryParseExact(last, "N", out var after))
                {
                    cursor = new ItemCursor(after, 0, keys);
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    private static string Encode(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private sealed record SortPosition(string[] Keys, string Id);
}

/// <summary>A list item as returned by the API. <c>fields</c> contains <c>title</c> and all field values.</summary>
public sealed record ItemResponse(
    Guid Id,
    Guid ListId,
    Guid ContentTypeId,
    Guid? ParentId,
    bool IsFolder,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedBy,
    JsonObject Fields)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }


    internal static ItemResponse From(ListItem item, IReadOnlyList<string>? select = null)
    {
        var fields = new JsonObject { ["title"] = item.Title };
        foreach (var property in JsonNode.Parse(item.Fields)!.AsObject().ToList())
        {
            fields[property.Key] = property.Value?.DeepClone();
        }

        if (select is not null)
        {
            foreach (var key in fields.Select(p => p.Key).Where(k => !select.Contains(k)).ToList())
            {
                fields.Remove(key);
            }
        }

        return new ItemResponse(item.Id, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, fields)
        {
            ETag = ETags.From(item.Version),
        };
    }
}

/// <summary>
/// Built-in parameter aliases for <c>$filter</c> (TAX-08): <c>@me</c> (the current user), <c>@now</c>, and
/// dates in UTC: <c>@today</c>, <c>@yesterday</c>, <c>@tomorrow</c>, <c>@weekStart</c>, <c>@weekEnd</c>
/// (Monday to Sunday), <c>@monthStart</c>, <c>@monthEnd</c>, <c>@last7Days</c>, <c>@next7Days</c>,
/// <c>@last30Days</c>, <c>@next30Days</c>. Example: <c>fields/dueDate le @next7Days and fields/assignedTo/any(p: p eq @me)</c>.
/// </summary>
internal static class QueryAliases
{
    public static Dictionary<string, string> For(Guid? userId, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@me"] = userId?.ToString() ?? "null",
            ["@now"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["@today"] = Date(today),
            ["@yesterday"] = Date(today.AddDays(-1)),
            ["@tomorrow"] = Date(today.AddDays(1)),
            ["@weekStart"] = Date(weekStart),
            ["@weekEnd"] = Date(weekStart.AddDays(6)),
            ["@monthStart"] = Date(monthStart),
            ["@monthEnd"] = Date(monthStart.AddMonths(1).AddDays(-1)),
            ["@last7Days"] = Date(today.AddDays(-7)),
            ["@next7Days"] = Date(today.AddDays(7)),
            ["@last30Days"] = Date(today.AddDays(-30)),
            ["@next30Days"] = Date(today.AddDays(30)),
        };
    }
}
