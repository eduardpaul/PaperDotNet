using System.Buffers.Text;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData;
using Microsoft.OData.UriParser;
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
    public const int MaxTop = 1000;

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
internal sealed class ItemQueryRunner(ListsDbContext db, FieldTypeRegistry fieldTypes, ITermStore terms)
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
            return (clause is null ? query : query.Where(translator.Filter(clause)), null);
        }
        catch (ODataException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Readable items (no folders) matching <paramref name="filter"/>, for code (<c>IListItemStore</c>).</summary>
    public async Task<(List<ListItem>? Items, string? Error)> ListAsync(ListSchema schema, string? filter, string? orderBy, int top, CancellationToken ct)
    {
        IQueryable<ListItem> query = db.Items.AsNoTracking().Where(i => i.ListId == schema.List.Id && !i.IsFolder);
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
                query = query.Where(translator.Filter(filterClause));
            }

            var ordered = orderByClause is null ? query.OrderBy(i => i.Id) : translator.OrderBy(query, orderByClause);
            return (await ordered.Take(Math.Clamp(top, 1, ItemQueryOptions.MaxTop)).ToListAsync(ct), null);
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
            (translator, var viewFilter, var viewOrder) = Parse(schema, view?.Filter, view?.OrderBy, hierarchy);
            (_, var filter, orderBy) = Parse(schema, options.Filter, options.OrderBy, hierarchy);
            orderBy ??= viewOrder;
            if (viewFilter is not null)
            {
                query = query.Where(translator.Filter(viewFilter));
            }

            if (filter is not null)
            {
                query = query.Where(translator.Filter(filter));
            }
        }
        catch (ODataException ex)
        {
            return (null, ex.Message);
        }

        long? count = options.Count ? await query.LongCountAsync(ct) : null;

        var cursor = ItemCursor.Decode(options.SkipToken);
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
            var next = orderBy is null ? ItemCursor.Keyset(items[^1].Id) : ItemCursor.ForOffset(cursor.Offset + options.Top);
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
        var translator = new ItemQueryTranslator(model, hierarchy);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
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

    private static string NextLink(HttpRequest request, string cursor)
    {
        var query = request.Query
            .Where(q => q.Key is not "$skiptoken")
            .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}")
            .Append($"$skiptoken={cursor}");
        return $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}?{string.Join('&', query)}";
    }
}

/// <summary>Opaque <c>$skiptoken</c>: keyset position (default order) or offset (custom <c>$orderby</c>).</summary>
internal readonly record struct ItemCursor(Guid? After, int Offset)
{
    public static string Keyset(Guid after) => Encode("k:" + after.ToString("N"));

    public static string ForOffset(int offset) => Encode("o:" + offset.ToString(CultureInfo.InvariantCulture));

    public static ItemCursor Decode(string? token)
    {
        if (token is null)
        {
            return default;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        }
        catch (FormatException)
        {
            return default;
        }

        if (text.StartsWith("k:", StringComparison.Ordinal) && Guid.TryParseExact(text[2..], "N", out var id))
        {
            return new ItemCursor(id, 0);
        }

        return text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text[2..], CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? new ItemCursor(null, offset)
            : default;
    }

    private static string Encode(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));
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

        return new ItemResponse(item.Id, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, fields);
    }
}
