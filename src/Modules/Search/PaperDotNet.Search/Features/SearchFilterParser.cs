using System.Globalization;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using PaperDotNet.Search.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>
/// Parses <c>$filter</c> of <c>/v1.0/search</c> (ADR-0043) into a store-neutral <see cref="SearchFilter"/>. Search
/// fields are properties of <c>fields</c>, typed by their kind: <c>fields/total gt 100 and fields/status in ('open',
/// 'late')</c>. A name used with several kinds is addressed as <c>{name}_{kind}</c>, e.g. <c>fields/total_keyword</c>.
/// Text kinds (keyword, reference, terms) support <c>eq</c>, <c>ne</c> and <c>in</c>; <c>eq null</c> matches documents
/// without the field.
/// </summary>
internal static class SearchFilterParser
{
    public const int MaxLength = 4000;

    private const string Namespace = "PaperDotNet";

    /// <summary>The <c>$filter</c> property names of the fields.</summary>
    public static IReadOnlyDictionary<string, SearchFieldInfo> Names(IReadOnlyList<SearchFieldInfo> fields)
    {
        var shared = fields.GroupBy(f => f.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return fields.ToDictionary(
            f => shared.Contains(f.Name) ? $"{f.Name}_{f.Kind.ToString().ToLowerInvariant()}" : f.Name,
            StringComparer.Ordinal);
    }

    /// <summary>The filter, or an <see cref="ArgumentException"/> saying what is wrong.</summary>
    public static SearchFilter Parse(string filter, IReadOnlyList<SearchFieldInfo> fields)
    {
        if (filter.Length > MaxLength)
        {
            throw new ArgumentException($"$filter is longer than {MaxLength} characters.");
        }

        var names = Names(fields);
        var model = new EdmModel();
        var fieldsType = new EdmComplexType(Namespace, "Fields");
        foreach (var (name, field) in names)
        {
            fieldsType.AddStructuralProperty(name, EdmKind(field.Kind), isNullable: true);
        }

        model.AddElement(fieldsType);
        var document = new EdmEntityType(Namespace, "SearchDocument");
        document.AddKeys(document.AddStructuralProperty("id", EdmPrimitiveTypeKind.Guid));
        document.AddStructuralProperty("fields", new EdmComplexTypeReference(fieldsType, isNullable: false));
        model.AddElement(document);
        var container = new EdmEntityContainer(Namespace, "Search");
        model.AddElement(container);
        var set = container.AddEntitySet("documents", document);
        try
        {
            var clause = new ODataQueryOptionParser(model, document, set, new Dictionary<string, string> { ["$filter"] = filter }).ParseFilter();
            return Translate(clause.Expression, names);
        }
        catch (ODataException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }
    }

    private static EdmPrimitiveTypeKind EdmKind(SearchFieldKind kind) => kind switch
    {
        SearchFieldKind.Keyword => EdmPrimitiveTypeKind.String,
        SearchFieldKind.Number => EdmPrimitiveTypeKind.Double,
        SearchFieldKind.Date => EdmPrimitiveTypeKind.Date,
        SearchFieldKind.DateTime => EdmPrimitiveTypeKind.DateTimeOffset,
        SearchFieldKind.Boolean => EdmPrimitiveTypeKind.Boolean,
        _ => EdmPrimitiveTypeKind.Guid,
    };

    private static SingleValueNode Unwrap(SingleValueNode node) => node is ConvertNode convert ? Unwrap(convert.Source) : node;

    private static SearchFilter Translate(SingleValueNode node, IReadOnlyDictionary<string, SearchFieldInfo> names)
    {
        switch (Unwrap(node))
        {
            case BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And or BinaryOperatorKind.Or } logical:
                var left = Translate(logical.Left, names);
                var right = Translate(logical.Right, names);
                return logical.OperatorKind == BinaryOperatorKind.And ? new SearchAnd(Flatten<SearchAnd>(left, right)) : new SearchOr(Flatten<SearchOr>(left, right));
            case UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } not:
                return new SearchNot(Translate(not.Operand, names));
            case ConstantNode { Value: bool value }:
                return value ? new SearchAnd([]) : new SearchOr([]);
            case BinaryOperatorNode comparison:
                return Comparison(comparison, names);
            case InNode @in:
                var field = Field(@in.Left, names);
                if (@in.Right is not CollectionConstantNode list)
                {
                    throw new ArgumentException("Use a list of values with in, e.g. fields/status in ('open', 'late').");
                }

                return new SearchIn(field.Name, field.Kind, list.Collection.Select(c => Value(c.Value, field)).ToList());
            default:
                throw new ArgumentException("Use comparisons (eq, ne, gt, ge, lt, le), in, and, or and not on fields/… in $filter.");
        }
    }

    private static List<SearchFilter> Flatten<T>(SearchFilter left, SearchFilter right)
        where T : SearchFilter
    {
        static IEnumerable<SearchFilter> Items(SearchFilter f) => f switch
        {
            SearchAnd all when typeof(T) == typeof(SearchAnd) => all.Items,
            SearchOr any when typeof(T) == typeof(SearchOr) => any.Items,
            _ => [f],
        };

        return [.. Items(left), .. Items(right)];
    }

    private static SearchFilter Comparison(BinaryOperatorNode node, IReadOnlyDictionary<string, SearchFieldInfo> names)
    {
        var (property, constant, op) = Unwrap(node.Left) is ConstantNode leftConstant
            ? (node.Right, leftConstant, Flip(node.OperatorKind))
            : (node.Left, Unwrap(node.Right) as ConstantNode, node.OperatorKind);
        if (constant is null)
        {
            throw new ArgumentException("Compare a field with a value, e.g. fields/total gt 100.");
        }

        var field = Field(property, names);
        if (constant.Value is null)
        {
            return op switch
            {
                BinaryOperatorKind.Equal => new SearchNot(new SearchHasValue(field.Name, field.Kind)),
                BinaryOperatorKind.NotEqual => new SearchHasValue(field.Name, field.Kind),
                _ => throw new ArgumentException("Only eq and ne compare with null."),
            };
        }

        var searchOperator = op switch
        {
            BinaryOperatorKind.Equal => SearchOperator.Equal,
            BinaryOperatorKind.NotEqual => SearchOperator.NotEqual,
            BinaryOperatorKind.GreaterThan => SearchOperator.GreaterThan,
            BinaryOperatorKind.GreaterThanOrEqual => SearchOperator.GreaterThanOrEqual,
            BinaryOperatorKind.LessThan => SearchOperator.LessThan,
            BinaryOperatorKind.LessThanOrEqual => SearchOperator.LessThanOrEqual,
            _ => throw new ArgumentException($"The operator {op} is not supported in search filters."),
        };
        if ((SearchField.IsText(field.Kind) || field.Kind == SearchFieldKind.Boolean) && searchOperator is not (SearchOperator.Equal or SearchOperator.NotEqual))
        {
            throw new ArgumentException($"fields/{field.Name} supports eq, ne and in only.");
        }

        return new SearchCompare(field.Name, field.Kind, searchOperator, Value(constant.Value, field));
    }

    private static BinaryOperatorKind Flip(BinaryOperatorKind op) => op switch
    {
        BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
        BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
        BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
        BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
        _ => op,
    };

    private static SearchFieldInfo Field(SingleValueNode node, IReadOnlyDictionary<string, SearchFieldInfo> names) =>
        Unwrap(node) is SingleValuePropertyAccessNode { Source: SingleComplexNode { Property.Name: "fields" } } access
            && names.TryGetValue(access.Property.Name, out var field)
            ? field
            : throw new ArgumentException("Filter on search fields: fields/{name}.");

    private static SearchValue Value(object? value, SearchFieldInfo field) => (field.Kind, value) switch
    {
        (_, null) => throw new ArgumentException($"null is only allowed with eq and ne ({field.Name})."),
        (SearchFieldKind.Keyword, string text) => SearchValue.Of(text),
        (SearchFieldKind.Reference or SearchFieldKind.Terms, Guid id) => SearchValue.Of(id),
        (SearchFieldKind.Boolean, bool flag) => SearchValue.Of(flag),
        (SearchFieldKind.Date, Date date) => SearchValue.Of(new DateOnly(date.Year, date.Month, date.Day)),
        (SearchFieldKind.Date, DateTimeOffset time) => SearchValue.Of(DateOnly.FromDateTime(time.UtcDateTime)),
        (SearchFieldKind.DateTime, DateTimeOffset time) => SearchValue.Of(time),
        (SearchFieldKind.DateTime, Date date) => SearchValue.Of(new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero)),
        (SearchFieldKind.Number, IConvertible number) when number is not string and not bool => SearchValue.Of(Convert.ToDouble(number, CultureInfo.InvariantCulture)),
        _ => throw new ArgumentException($"The value {value} does not fit fields/{field.Name} ({field.Kind.ToString().ToLowerInvariant()})."),
    };
}
