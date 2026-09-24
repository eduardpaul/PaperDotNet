using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists.Querying;

/// <summary>
/// Translates parsed OData <c>$filter</c>/<c>$orderby</c> trees into LINQ over
/// <see cref="ListItem"/>. Field values are read from the JSON document through the
/// provider-neutral <see cref="JsonFunctions"/>; equality uses JSON containment so
/// PostgreSQL can use its GIN index. Equality on a managed metadata field also
/// matches the term's descendants (<paramref name="termDescendants"/>).
/// </summary>
internal sealed class ItemQueryTranslator(ItemEdmModel model, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? termDescendants = null)
{
    private static readonly ParameterExpression Item = Expression.Parameter(typeof(ListItem), "i");
    private static readonly MethodInfo JsonText = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Text))!;
    private static readonly MethodInfo JsonNumber = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Number))!;
    private static readonly MethodInfo JsonBoolean = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Boolean))!;
    private static readonly MethodInfo JsonContains = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Contains))!;
    private static readonly MethodInfo JsonHasProperty = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.HasProperty))!;
    private static readonly MethodInfo StringCompare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo StringToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
    private static readonly MethodInfo StringToUpper = typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!;

    private static readonly Expression Document = Expression.Property(Item, nameof(ListItem.Fields));

    public Expression<Func<ListItem, bool>> Filter(FilterClause filter) =>
        Expression.Lambda<Func<ListItem, bool>>(Predicate(filter.Expression), Item);

    public IOrderedQueryable<ListItem> OrderBy(IQueryable<ListItem> query, OrderByClause? clause)
    {
        IOrderedQueryable<ListItem>? ordered = null;
        for (var current = clause; current is not null; current = current.ThenBy)
        {
            var key = Operand(current.Expression).Expression;
            var lambda = Expression.Lambda(key, Item);
            var descending = current.Direction == OrderByDirection.Descending;
            var method = ordered is null
                ? (descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy))
                : (descending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy));
            var source = ordered?.Expression ?? query.Expression;
            var call = Expression.Call(typeof(Queryable), method, [typeof(ListItem), key.Type], source, Expression.Quote(lambda));
            ordered = (IOrderedQueryable<ListItem>)query.Provider.CreateQuery<ListItem>(call);
        }

        // Stable order for paging.
        return ordered is null ? query.OrderBy(i => i.Id) : ordered.ThenBy(i => i.Id);
    }

    private Expression Predicate(QueryNode node) => node switch
    {
        ConvertNode convert => Predicate(convert.Source),
        BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And } b => Expression.AndAlso(Predicate(b.Left), Predicate(b.Right)),
        BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Or } b => Expression.OrElse(Predicate(b.Left), Predicate(b.Right)),
        BinaryOperatorNode b => Comparison(b),
        UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } u => Expression.Not(Predicate(u.Operand)),
        SingleValueFunctionCallNode f => StringFunction(f),
        InNode inNode => In(inNode),
        AnyNode any => Any(any),
        SingleValuePropertyAccessNode => Operand(node) is { Kind: FieldValueKind.Boolean } boolean
            ? Expression.Equal(boolean.Expression, Expression.Constant(true, boolean.Expression.Type))
            : throw Unsupported("Only boolean properties can be used as conditions."),
        _ => throw Unsupported($"'{node.Kind}' is not supported in $filter."),
    };

    private Expression Comparison(BinaryOperatorNode node)
    {
        var (propertyNode, constantNode, flipped) = Unwrap(node.Left) is ConstantNode
            ? (node.Right, (ConstantNode)Unwrap(node.Left), true)
            : (node.Left, Unwrap(node.Right) as ConstantNode, false);
        if (constantNode is null)
        {
            throw Unsupported("Comparisons must compare a property with a literal value.");
        }

        var kind = flipped ? Flip(node.OperatorKind) : node.OperatorKind;
        var operand = Operand(propertyNode);

        if (constantNode.Value is null)
        {
            Expression isNull = operand.JsonField is { } nullField
                ? Expression.Not(Expression.Call(JsonHasProperty, Document, Expression.Constant(nullField)))
                : Expression.Equal(operand.Expression, Expression.Constant(null, operand.Expression.Type));
            return kind switch
            {
                BinaryOperatorKind.Equal => isNull,
                BinaryOperatorKind.NotEqual => Expression.Not(isNull),
                _ => throw Unsupported("null can only be compared with eq or ne."),
            };
        }

        // Equality on JSON fields uses containment (@>) so the GIN index applies.
        if (operand.JsonField is { } field && kind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual)
        {
            var contains = Matches(field, operand.Kind, constantNode.Value, multiple: false);
            return kind == BinaryOperatorKind.Equal ? contains : Expression.Not(contains);
        }

        var constant = Literal(operand, constantNode.Value);
        if (operand.Kind is FieldValueKind.Text or FieldValueKind.Date or FieldValueKind.DateTime
            || (operand.Kind == FieldValueKind.Identifier && operand.Expression.Type == typeof(string)))
        {
            var compare = Expression.Call(StringCompare, operand.Expression, constant);
            return Compare(kind, compare, Expression.Constant(0));
        }

        return Compare(kind, operand.Expression, constant);
    }

    private static BinaryExpression Compare(BinaryOperatorKind kind, Expression left, Expression right) => kind switch
    {
        BinaryOperatorKind.Equal => Expression.Equal(left, right),
        BinaryOperatorKind.NotEqual => Expression.NotEqual(left, right),
        BinaryOperatorKind.GreaterThan => Expression.GreaterThan(left, right),
        BinaryOperatorKind.GreaterThanOrEqual => Expression.GreaterThanOrEqual(left, right),
        BinaryOperatorKind.LessThan => Expression.LessThan(left, right),
        BinaryOperatorKind.LessThanOrEqual => Expression.LessThanOrEqual(left, right),
        _ => throw Unsupported($"Operator '{kind}' is not supported."),
    };

    private MethodCallExpression StringFunction(SingleValueFunctionCallNode node)
    {
        var method = node.Name switch
        {
            "contains" => StringContains,
            "startswith" => StringStartsWith,
            "endswith" => StringEndsWith,
            _ => throw Unsupported($"Function '{node.Name}' is not supported."),
        };
        var arguments = node.Parameters.ToList();
        var target = StringOperand(arguments[0]);
        if (Unwrap(arguments[1]) is not ConstantNode { Value: string text })
        {
            throw Unsupported($"The second argument of {node.Name} must be a text literal.");
        }

        return Expression.Call(target, method, Expression.Constant(text));
    }

    private Expression StringOperand(QueryNode node)
    {
        node = Unwrap(node);
        if (node is SingleValueFunctionCallNode { Name: "tolower" or "toupper" } call)
        {
            var inner = StringOperand(call.Parameters.First());
            return Expression.Call(inner, call.Name == "tolower" ? StringToLower : StringToUpper);
        }

        var operand = Operand(node);
        return operand.Kind == FieldValueKind.Text
            ? operand.Expression
            : throw Unsupported("Text functions need a text property.");
    }

    private Expression In(InNode node)
    {
        var operand = Operand(node.Left);
        if (node.Right is not CollectionConstantNode values)
        {
            throw Unsupported("'in' needs a list of literals.");
        }

        var tests = values.Collection.Select(v => operand.JsonField is { } field
            ? Matches(field, operand.Kind, v.Value, multiple: false)
            : Expression.Equal(operand.Expression, Literal(operand, v.Value)));
        return tests.Aggregate<Expression, Expression>(Expression.Constant(false), Expression.OrElse);
    }

    /// <summary><c>fields/tags/any(t: t eq 'a' or t eq 'b')</c> on multi-value fields.</summary>
    private Expression Any(AnyNode node)
    {
        if (node.Source is not CollectionPropertyAccessNode { Source: SingleComplexNode } collection
            || !model.Fields.TryGetValue(collection.Property.Name, out var field))
        {
            throw Unsupported("'any' is only supported on multi-value fields.");
        }

        return AnyBody(node.Body, field.Field.Name, field.Type.ValueKind);
    }

    private Expression AnyBody(QueryNode body, string field, FieldValueKind kind)
    {
        body = Unwrap(body);
        return body switch
        {
            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Or } or => Expression.OrElse(AnyBody(or.Left, field, kind), AnyBody(or.Right, field, kind)),
            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Equal } eq
                when Unwrap(eq.Left) is NonResourceRangeVariableReferenceNode && Unwrap(eq.Right) is ConstantNode value
                => Matches(field, kind, value.Value, multiple: true),
            _ => throw Unsupported("Inside 'any' only 'eq' comparisons combined with 'or' are supported."),
        };
    }

    /// <summary>The field holds the value; for managed metadata, the term or one of its descendants.</summary>
    private Expression Matches(string field, FieldValueKind kind, object? value, bool multiple)
    {
        if (value is Guid termId
            && termDescendants is not null
            && model.Fields.TryGetValue(field, out var definition)
            && definition.Type.Name == ManagedMetadataFieldType.TypeName
            && termDescendants.TryGetValue(termId, out var subtree)
            && subtree.Count > 0)
        {
            return subtree
                .Select(id => (Expression)FieldContains(field, JsonValue.Create(FieldFormats.Identifier(id)), multiple))
                .Aggregate(Expression.OrElse);
        }

        return FieldContains(field, JsonLiteral(kind, value), multiple);
    }

    private static MethodCallExpression FieldContains(string field, JsonNode? value, bool multiple)
    {
        var fragment = new JsonObject { [field] = multiple ? new JsonArray(value) : value };
        return Expression.Call(JsonContains, Document, Expression.Constant(fragment.ToJsonString()));
    }

    private (Expression Expression, FieldValueKind Kind, string? JsonField) Operand(QueryNode node)
    {
        node = Unwrap(node);
        if (node is not SingleValuePropertyAccessNode property)
        {
            throw Unsupported("Expected a property.");
        }

        var name = property.Property.Name;
        if (property.Source is SingleComplexNode)
        {
            if (name == "title")
            {
                return (Expression.Property(Item, nameof(ListItem.Title)), FieldValueKind.Text, null);
            }

            if (!model.Fields.TryGetValue(name, out var field) || field.Field.AllowMultiple)
            {
                throw Unsupported($"Field '{name}' cannot be used here (multi-value fields need 'any').");
            }

            var fieldName = Expression.Constant(name);
            return field.Type.ValueKind switch
            {
                FieldValueKind.Number => (Expression.Call(JsonNumber, Document, fieldName), FieldValueKind.Number, name),
                FieldValueKind.Boolean => (Expression.Call(JsonBoolean, Document, fieldName), FieldValueKind.Boolean, name),
                var kind => (Expression.Call(JsonText, Document, fieldName), kind, name),
            };
        }

        return name switch
        {
            "id" => (Expression.Property(Item, nameof(ListItem.Id)), FieldValueKind.Identifier, null),
            "contentTypeId" => (Expression.Property(Item, nameof(ListItem.ContentTypeId)), FieldValueKind.Identifier, null),
            "parentId" => (Expression.Property(Item, nameof(ListItem.ParentId)), FieldValueKind.Identifier, null),
            "isFolder" => (Expression.Property(Item, nameof(ListItem.IsFolder)), FieldValueKind.Boolean, null),
            "createdAt" => (Expression.Property(Item, nameof(ListItem.CreatedAt)), FieldValueKind.DateTime, null),
            "updatedAt" => (Expression.Property(Item, nameof(ListItem.UpdatedAt)), FieldValueKind.DateTime, null),
            "createdBy" => (Expression.Property(Item, nameof(ListItem.CreatedBy)), FieldValueKind.Identifier, null),
            "updatedBy" => (Expression.Property(Item, nameof(ListItem.UpdatedBy)), FieldValueKind.Identifier, null),
            _ => throw Unsupported($"Property '{name}' cannot be queried."),
        };
    }

    /// <summary>Constant for a LINQ comparison, typed like the operand.</summary>
    private static ConstantExpression Literal((Expression Expression, FieldValueKind Kind, string? JsonField) operand, object? value)
    {
        if (operand.Expression.Type == typeof(DateTimeOffset))
        {
            return Expression.Constant(ToDateTimeOffset(value));
        }

        if (operand.Expression.Type == typeof(Guid) || operand.Expression.Type == typeof(Guid?))
        {
            return Expression.Constant(value is Guid g ? g : throw Unsupported("A GUID literal is expected."), operand.Expression.Type);
        }

        return operand.Kind switch
        {
            FieldValueKind.Number => Expression.Constant(Convert.ToDouble(value, CultureInfo.InvariantCulture), typeof(double?)),
            FieldValueKind.Boolean => Expression.Constant(value is bool b ? b : throw Unsupported("true or false is expected."), typeof(bool?)),
            _ => Expression.Constant(JsonLiteral(operand.Kind, value)!.GetValue<string>()),
        };
    }

    /// <summary>A literal in the canonical storage form of the field kind.</summary>
    private static JsonValue? JsonLiteral(FieldValueKind kind, object? value) => kind switch
    {
        FieldValueKind.Number => JsonValue.Create(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
        FieldValueKind.Boolean => JsonValue.Create(value is bool b ? b : throw Unsupported("true or false is expected.")),
        FieldValueKind.Date => JsonValue.Create(value switch
        {
            Microsoft.OData.Edm.Date d => FieldFormats.Date(new DateOnly(d.Year, d.Month, d.Day)),
            DateTimeOffset dto => FieldFormats.Date(DateOnly.FromDateTime(dto.UtcDateTime)),
            _ => throw Unsupported("A date literal (yyyy-mm-dd) is expected."),
        }),
        FieldValueKind.DateTime => JsonValue.Create(FieldFormats.DateTime(ToDateTimeOffset(value))),
        FieldValueKind.Identifier => JsonValue.Create(value is Guid g ? FieldFormats.Identifier(g) : throw Unsupported("A GUID literal is expected.")),
        _ => JsonValue.Create(value as string ?? throw Unsupported("A text literal is expected.")),
    };

    private static DateTimeOffset ToDateTimeOffset(object? value) => value switch
    {
        DateTimeOffset dto => dto,
        Microsoft.OData.Edm.Date d => new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero),
        _ => throw Unsupported("A date-time literal is expected."),
    };

    private static QueryNode Unwrap(QueryNode node) => node is ConvertNode convert ? Unwrap(convert.Source) : node;

    private static BinaryOperatorKind Flip(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
        BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
        BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
        BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
        _ => kind,
    };

    private static ODataException Unsupported(string message) => new(message);
}
