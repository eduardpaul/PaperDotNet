using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Fields;

namespace PaperDotNet.Lists.Querying;

/// <summary>Which items of the lists a query reads.</summary>
internal enum FolderMode
{
    /// <summary>Items only, anywhere in the list (queries and the store).</summary>
    ItemsOnly,

    /// <summary>Items and folders directly in <see cref="ItemQuery.ParentId"/> (null: the list root).</summary>
    Children,

    /// <summary>Items and folders anywhere (the items API without a folder).</summary>
    All,
}

/// <summary>
/// A parsed item query over one or more lists of the same shape: OData <c>$filter</c> clauses (all must hold) and
/// <c>$orderby</c>, validated against the lists' fields by the OData URI parser. Microsoft.OData.Core's parser runs
/// under Native AOT; the parsed tree is translated to SQL by the provider (<see cref="IItemQueries"/>), not to LINQ:
/// EF Core cannot run dynamic LINQ under AOT (ADR-0039). <c>Scopes</c> are the permission scopes the caller may
/// read (null for all, with full control).
/// </summary>
internal sealed record ItemQuery(
    Guid TenantId,
    IReadOnlyList<Guid> ListIds,
    ParsedItemQuery Parsed,
    IReadOnlyList<Guid>? Scopes,
    FolderMode Folders,
    Guid? ParentId,
    ItemCursor Cursor,
    int Top,
    bool Count,
    Guid? ItemId = null);

internal sealed record ItemQueryResult(IReadOnlyList<Data.ListItem> Items, long? Count, bool HasMore);

/// <summary>Runs item queries; one implementation per database provider.</summary>
internal interface IItemQueries
{
    Task<ItemQueryResult> QueryAsync(ItemQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the children of <paramref name="parentId"/> that inherit <paramref name="oldScope"/> (also in the recycle
    /// bin) to <paramref name="newScope"/> in one statement, without touching their versions (ADR-0035). One bulk
    /// update: EF Core cannot precompile <c>ExecuteUpdate</c> (ADR-0039).
    /// </summary>
    Task<int> MoveScopeAsync(Guid tenantId, Guid parentId, Guid oldScope, Guid newScope, CancellationToken cancellationToken);

    /// <summary>
    /// Counts the items matching <paramref name="query"/> per value of <paramref name="field"/> (at most
    /// <see cref="ItemQuery.Top"/> values, most frequent first); with <paramref name="multiple"/> an item counts once
    /// per value, and items without a value are one more entry with a null value.
    /// </summary>
    Task<IReadOnlyList<Features.ValueCount>> CountValuesAsync(ItemQuery query, string field, bool multiple, CancellationToken cancellationToken);
}

/// <summary>Parsed <c>$filter</c> clauses and <c>$orderby</c> with the aliases they refer to.</summary>
internal sealed record ParsedItemQuery(
    IReadOnlyList<(FilterClause Clause, IDictionary<string, QueryNode> Aliases)> Filters,
    OrderByClause? OrderBy,
    IDictionary<string, QueryNode>? OrderAliases)
{
    public static readonly ParsedItemQuery Empty = new([], null, null);
}

/// <summary>
/// OData (EDM) model of a list, built from its fields, so the OData parser validates <c>$filter</c>/<c>$orderby</c>
/// against the real schema. Items look like Graph list items: top-level properties plus <c>fields/…</c>.
/// </summary>
internal static class ItemEdmModel
{
    public const string Namespace = "PaperDotNet";

    /// <summary>Top-level item properties and their EDM types.</summary>
    public static readonly IReadOnlyDictionary<string, EdmPrimitiveTypeKind> ItemProperties = new Dictionary<string, EdmPrimitiveTypeKind>(StringComparer.Ordinal)
    {
        ["id"] = EdmPrimitiveTypeKind.Guid,
        ["contentTypeId"] = EdmPrimitiveTypeKind.Guid,
        ["parentId"] = EdmPrimitiveTypeKind.Guid,
        ["isFolder"] = EdmPrimitiveTypeKind.Boolean,
        ["createdAt"] = EdmPrimitiveTypeKind.DateTimeOffset,
        ["updatedAt"] = EdmPrimitiveTypeKind.DateTimeOffset,
        ["createdBy"] = EdmPrimitiveTypeKind.Guid,
        ["updatedBy"] = EdmPrimitiveTypeKind.Guid,
    };

    public static (EdmModel Model, EdmEntityType ItemType, EdmEntitySet Items) Build(IReadOnlyDictionary<string, FieldDefinition> fields, FieldTypeRegistry registry)
    {
        var model = new EdmModel();
        var fieldsType = new EdmComplexType(Namespace, "Fields");
        fieldsType.AddStructuralProperty("title", EdmPrimitiveTypeKind.String);
        foreach (var field in fields.Values)
        {
            if (registry.Find(field.Type) is not { } type)
            {
                continue;
            }

            var primitive = EdmCoreModel.Instance.GetPrimitive(EdmKindOf(type.ValueKind), isNullable: true);
            IEdmTypeReference reference = field.AllowMultiple ? new EdmCollectionTypeReference(new EdmCollectionType(primitive)) : primitive;
            fieldsType.AddStructuralProperty(field.Name, reference);
        }

        model.AddElement(fieldsType);
        var itemType = new EdmEntityType(Namespace, "Item");
        foreach (var (name, kind) in ItemProperties)
        {
            var property = itemType.AddStructuralProperty(name, EdmCoreModel.Instance.GetPrimitive(kind, isNullable: name is "parentId" or "createdBy" or "updatedBy"));
            if (name == "id")
            {
                itemType.AddKeys(property);
            }
        }

        itemType.AddStructuralProperty("fields", new EdmComplexTypeReference(fieldsType, isNullable: false));
        model.AddElement(itemType);
        var container = new EdmEntityContainer(Namespace, "Container");
        model.AddElement(container);
        return (model, itemType, container.AddEntitySet("items", itemType));
    }

    /// <summary>The OData type used to parse literals for a field value kind.</summary>
    public static EdmPrimitiveTypeKind EdmKindOf(FieldValueKind kind) => kind switch
    {
        FieldValueKind.Number => EdmPrimitiveTypeKind.Decimal,
        FieldValueKind.Boolean => EdmPrimitiveTypeKind.Boolean,
        FieldValueKind.Date => EdmPrimitiveTypeKind.Date,
        FieldValueKind.DateTime => EdmPrimitiveTypeKind.DateTimeOffset,
        FieldValueKind.Identifier => EdmPrimitiveTypeKind.Guid,
        _ => EdmPrimitiveTypeKind.String,
    };
}

/// <summary>Parses item queries against a list's fields.</summary>
internal static class ItemQueryParser
{
    /// <summary>
    /// Parses <c>$filter</c> clauses (each on its own, so their aliases stay apart) and <c>$orderby</c>; the error is a
    /// message for a 400.
    /// </summary>
    public static (ParsedItemQuery? Query, string? Error) Parse(
        IReadOnlyDictionary<string, FieldDefinition> fields, FieldTypeRegistry registry, IEnumerable<string?> filters, string? orderBy,
        Guid? userId, DateTimeOffset now)
    {
        var (model, itemType, items) = ItemEdmModel.Build(fields, registry);
        try
        {
            var parsedFilters = new List<(FilterClause, IDictionary<string, QueryNode>)>();
            foreach (var filter in filters.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                var options = QueryAliases.For(userId, now);
                options["$filter"] = filter!;
                var parser = new ODataQueryOptionParser(model, itemType, items, options);
                if (parser.ParseFilter() is { } clause)
                {
                    parsedFilters.Add((clause, parser.ParameterAliasAllNodes));
                }
            }

            OrderByClause? order = null;
            IDictionary<string, QueryNode>? orderAliases = null;
            if (!string.IsNullOrWhiteSpace(orderBy))
            {
                var options = QueryAliases.For(userId, now);
                options["$orderby"] = orderBy;
                var parser = new ODataQueryOptionParser(model, itemType, items, options);
                order = parser.ParseOrderBy();
                orderAliases = parser.ParameterAliasAllNodes;
            }

            return (new ParsedItemQuery(parsedFilters, order, orderAliases), null);
        }
        catch (ODataException exception)
        {
            return (null, exception.Message);
        }
    }
}

/// <summary>
/// Built-in parameter aliases for <c>$filter</c> (TAX-08): <c>@me</c> (the current user), <c>@now</c>, and dates in
/// UTC: <c>@today</c>, <c>@yesterday</c>, <c>@tomorrow</c>, <c>@weekStart</c>, <c>@weekEnd</c> (Monday to Sunday),
/// <c>@monthStart</c>, <c>@monthEnd</c>, <c>@last7Days</c>, <c>@next7Days</c>, <c>@last30Days</c>, <c>@next30Days</c>.
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

/// <summary>Opaque <c>$skiptoken</c>: keyset position (default order: the last id) or offset (custom <c>$orderby</c>).</summary>
internal readonly record struct ItemCursor(Guid? After, int Offset)
{
    public static string Keyset(Guid after) => Encode("k:" + after.ToString("N"));

    public static string ForOffset(int offset) => Encode("o:" + offset.ToString(CultureInfo.InvariantCulture));

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

        if (text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            cursor = new ItemCursor(null, offset);
            return true;
        }

        return false;
    }

    private static string Encode(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));
}
