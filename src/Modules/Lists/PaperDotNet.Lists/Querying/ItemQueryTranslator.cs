using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists.Querying;

/// <summary>
/// Translates parsed OData <c>$filter</c>/<c>$orderby</c> trees into LINQ over
/// <see cref="ListItem"/>. Indexed fields (ADR-0035) use their item column or the value table; other field values
/// are read from the JSON document through the provider-neutral <see cref="JsonFunctions"/>, where equality uses JSON
/// containment so PostgreSQL can use its GIN index. Equality on a managed metadata field also matches the term's
/// descendants (<paramref name="termDescendants"/>).
/// </summary>
internal sealed class ItemQueryTranslator(
    ItemEdmModel model,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? termDescendants = null,
    IDictionary<string, SingleValueNode>? aliases = null,
    ListDefinition? list = null,
    IQueryable<ItemValue>? values = null)
{
    /// <summary>
    /// Value-table filters known to match few items (field number and values, see <see cref="ValueFilters"/>): those
    /// read the matching item ids first (<c>IN</c>); the others test each item (<c>EXISTS</c>). SQLite needs the choice.
    /// </summary>
    public HashSet<string> Selective { get; } = new(StringComparer.Ordinal);

    internal static string SelectivityKey(short field, IEnumerable<Guid> ids) => $"{field}:{string.Join(',', ids.Order())}";

    /// <summary>The lists the query covers (one query over lists of the same shape, ADR-0035); the list itself by default.</summary>
    public Guid[]? ListIds { get; set; }

    private static readonly ParameterExpression Item = Expression.Parameter(typeof(ListItem), "i");
    internal static readonly MethodInfo JsonText = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Text))!;
    private static readonly MethodInfo JsonNumber = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Number))!;
    private static readonly MethodInfo JsonBoolean = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Boolean))!;
    private static readonly MethodInfo JsonContains = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Contains))!;
    internal static readonly MethodInfo JsonHasProperty = typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.HasProperty))!;
    internal static readonly MethodInfo StringSubstring = typeof(string).GetMethod(nameof(string.Substring), [typeof(int), typeof(int)])!;
    private static readonly MethodInfo StringCompare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo StringToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
    private static readonly MethodInfo StringToUpper = typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!;

    private static readonly Expression Document = Expression.Property(Item, nameof(ListItem.Fields));

    public Expression<Func<ListItem, bool>> Filter(FilterClause filter) =>
        Expression.Lambda<Func<ListItem, bool>>(Predicate(filter.Expression), Item);

    /// <summary>
    /// The settable part of a filter (TAX-09): <c>fields/x eq value</c> comparisons joined with <c>and</c>, as field
    /// values in storage form (<c>title</c> included). Other conditions (ranges, <c>or</c>, relative dates) are ignored.
    /// </summary>
    public JsonObject Equalities(FilterClause filter)
    {
        var values = new JsonObject();
        void Walk(QueryNode node)
        {
            node = Unwrap(node);
            if (node is BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And } and)
            {
                Walk(and.Left);
                Walk(and.Right);
                return;
            }

            if (node is not BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Equal } eq)
            {
                return;
            }

            var (property, constant) = Unwrap(eq.Left) is ConstantNode left ? (Unwrap(eq.Right), left) : (Unwrap(eq.Left), Unwrap(eq.Right) as ConstantNode);
            if (constant is null || property is not SingleValuePropertyAccessNode { Source: SingleComplexNode } access)
            {
                return;
            }

            var name = access.Property.Name;
            if (name == "title")
            {
                values[name] = constant.Value as string;
            }
            else if (model.Fields.TryGetValue(name, out var field) && !field.Field.AllowMultiple)
            {
                values[name] = constant.Value is null ? null : JsonLiteral(field.Type.ValueKind, constant.Value);
            }
        }

        Walk(filter.Expression);
        return values;
    }

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

    /// <summary>
    /// The order's keys when every one is <c>isFolder</c> or <c>fields/title</c> (the list screen's order), so pages can
    /// continue after the last row; null for other orders.
    /// </summary>
    public static IReadOnlyList<(string Property, bool Descending)>? SortKeys(OrderByClause clause)
    {
        var keys = new List<(string, bool)>();
        for (var current = clause; current is not null; current = current.ThenBy)
        {
            var node = current.Expression is ConvertNode convert ? convert.Source : current.Expression;
            if (node is not SingleValuePropertyAccessNode property)
            {
                return null;
            }

            var name = property.Source is SingleComplexNode
                ? property.Property.Name == "title" ? nameof(ListItem.Title) : null
                : property.Property.Name == "isFolder" ? nameof(ListItem.IsFolder) : null;
            if (name is null)
            {
                return null;
            }

            keys.Add((name, current.Direction == OrderByDirection.Descending));
        }

        return keys;
    }

    /// <summary>The sort key values of a row, for the next page's cursor.</summary>
    public static List<string> KeyValues(IReadOnlyList<(string Property, bool Descending)> keys, ListItem item) =>
        [.. keys.Select(k => k.Property == nameof(ListItem.IsFolder) ? (item.IsFolder ? "true" : "false") : item.Title)];

    /// <summary>Rows after the position (<paramref name="values"/>, <paramref name="after"/>) in the order of <paramref name="keys"/> then id.</summary>
    public static Expression<Func<ListItem, bool>> After(IReadOnlyList<(string Property, bool Descending)> keys, IReadOnlyList<string> values, Guid after)
    {
        Expression result = Expression.GreaterThan(
            Expression.Call(Expression.Property(Item, nameof(ListItem.Id)), typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!, Expression.Constant(after)),
            Expression.Constant(0));
        for (var n = keys.Count - 1; n >= 0; n--)
        {
            var (property, descending) = keys[n];
            var member = Expression.Property(Item, property);
            Expression equal;
            Expression beyond;
            if (property == nameof(ListItem.IsFolder))
            {
                var value = values[n] == "true";
                equal = Expression.Equal(member, Expression.Constant(value));

                // false sorts before true: ascending, only true follows false; descending, only false follows true.
                beyond = descending == value ? Expression.Equal(member, Expression.Constant(!value)) : Expression.Constant(false);
            }
            else
            {
                var value = Expression.Constant(values[n]);
                equal = Expression.Equal(member, value);
                var compare = Expression.Call(StringCompare, member, value);
                beyond = descending ? Expression.LessThan(compare, Expression.Constant(0)) : Expression.GreaterThan(compare, Expression.Constant(0));
            }

            result = Expression.OrElse(beyond, Expression.AndAlso(equal, result));
        }

        return Expression.Lambda<Func<ListItem, bool>>(result, Item);
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
            ? Expression.Equal(boolean.Expression, Literal(boolean, true))
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
        if (ValueField(propertyNode) is { } indexed)
        {
            // A single person, lookup or term in the value table.
            Expression test = constantNode.Value is null
                ? Expression.Not(HasValues(indexed.Entry, null))
                : HasValues(indexed.Entry, Ids(indexed.Entry.Field, indexed.Kind, [constantNode.Value]));
            return kind switch
            {
                BinaryOperatorKind.Equal => test,
                BinaryOperatorKind.NotEqual => Expression.Not(test),
                _ => throw Unsupported($"Field '{indexed.Entry.Field}' can only be compared with eq or ne."),
            };
        }

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
        if (ValueField(node.Left) is { } indexed && node.Right is CollectionConstantNode listed)
        {
            return HasValues(indexed.Entry, Ids(indexed.Entry.Field, indexed.Kind, listed.Collection.Select(v => v.Value)));
        }

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

        if (list is not null && values is not null && FieldIndex.Ready(list, field.Field.Name) is { Kind: IndexKind.Values } indexed)
        {
            var literals = new List<object?>();
            CollectAny(node.Body, literals);
            return HasValues(indexed, Ids(field.Field.Name, field.Type.ValueKind, literals));
        }

        return AnyBody(node.Body, field.Field.Name, field.Type.ValueKind);
    }

    /// <summary>The literals of an <c>any</c> body made of <c>eq</c> comparisons joined with <c>or</c>.</summary>
    private void CollectAny(QueryNode body, List<object?> literals)
    {
        body = Unwrap(body);
        switch (body)
        {
            case BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Or } or:
                CollectAny(or.Left, literals);
                CollectAny(or.Right, literals);
                break;
            case BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Equal } eq
                when Unwrap(eq.Left) is NonResourceRangeVariableReferenceNode && Unwrap(eq.Right) is ConstantNode value:
                literals.Add(value.Value);
                break;
            default:
                throw Unsupported("Inside 'any' only 'eq' comparisons combined with 'or' are supported.");
        }
    }

    /// <summary>A single-value field stored in the value table (indexed and ready), or null.</summary>
    private (IndexedField Entry, FieldValueKind Kind)? ValueField(QueryNode node) =>
        list is not null && values is not null
        && Unwrap(node) is SingleValuePropertyAccessNode { Source: SingleComplexNode } property
        && model.Fields.TryGetValue(property.Property.Name, out var field)
        && FieldIndex.Ready(list, field.Field.Name) is { Kind: IndexKind.Values } entry
            ? (entry, field.Type.ValueKind)
            : null;

    /// <summary>Value-table ids of literals; a managed metadata term also matches its descendants.</summary>
    private Guid[] Ids(string field, FieldValueKind kind, IEnumerable<object?> literals)
    {
        var ids = new HashSet<Guid>();
        var terms = model.Fields.TryGetValue(field, out var definition) && definition.Type.Name == ManagedMetadataFieldType.TypeName;
        foreach (var literal in literals)
        {
            var text = JsonLiteral(kind, literal)?.GetValue<string>() ?? throw Unsupported("A value is expected.");
            var id = FieldIndex.ValueId(field, text);
            ids.Add(id);
            if (terms && termDescendants is not null && termDescendants.TryGetValue(id, out var subtree))
            {
                ids.UnionWith(subtree);
            }
        }

        return [.. ids];
    }

    /// <summary>
    /// The item has one of <paramref name="ids"/> (any value when null) in the value table, as <c>IN</c> for
    /// selective values and <c>EXISTS</c> otherwise (see <see cref="Selective"/>).
    /// </summary>
    private Expression HasValues(IndexedField entry, Guid[]? ids)
    {
        var source = values!;
        var listIds = ListIds ?? [list!.Id];
        var field = entry.ValueField!.Value;
        Expression<Func<ListItem, bool>> test;
        if (ids is null)
        {
            test = i => source.Any(v => v.ItemId == i.Id && v.Field == field);
        }
        else if (Selective.Contains(SelectivityKey(field, ids)))
        {
            test = i => source.Where(v => listIds.Contains(v.ListId) && v.Field == field && ids.Contains(v.Value)).Select(v => v.ItemId).Contains(i.Id);
        }
        else
        {
            test = i => source.Any(v => v.ItemId == i.Id && v.Field == field && ids.Contains(v.Value));
        }

        return new ItemFields.ReplaceParameter(test.Parameters[0], Item).Visit(test.Body)!;
    }

    /// <summary>
    /// The value-table filters of a clause (field number and ids), so the caller can count how many items match
    /// each and mark the selective ones before the query runs.
    /// </summary>
    public IEnumerable<(short Field, Guid[] Ids)> ValueFilters(FilterClause clause)
    {
        var found = new List<(short, Guid[])>();
        void Walk(QueryNode node)
        {
            node = Unwrap(node);
            switch (node)
            {
                case BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And or BinaryOperatorKind.Or } logical:
                    Walk(logical.Left);
                    Walk(logical.Right);
                    break;
                case UnaryOperatorNode unary:
                    Walk(unary.Operand);
                    break;
                case BinaryOperatorNode comparison:
                    var (property, constant) = Unwrap(comparison.Left) is ConstantNode left ? (comparison.Right, left) : (comparison.Left, Unwrap(comparison.Right) as ConstantNode);
                    if (constant?.Value is not null && ValueField(property) is { } single)
                    {
                        found.Add((single.Entry.ValueField!.Value, Ids(single.Entry.Field, single.Kind, [constant.Value])));
                    }

                    break;
                case InNode inNode when inNode.Right is CollectionConstantNode listed && ValueField(inNode.Left) is { } inField:
                    found.Add((inField.Entry.ValueField!.Value, Ids(inField.Entry.Field, inField.Kind, listed.Collection.Select(v => v.Value))));
                    break;
                case AnyNode { Source: CollectionPropertyAccessNode { Source: SingleComplexNode } collection } any
                    when list is not null && model.Fields.TryGetValue(collection.Property.Name, out var field)
                         && FieldIndex.Ready(list, field.Field.Name) is { Kind: IndexKind.Values } entry:
                    var literals = new List<object?>();
                    CollectAny(any.Body, literals);
                    found.Add((entry.ValueField!.Value, Ids(field.Field.Name, field.Type.ValueKind, literals)));
                    break;
            }
        }

        Walk(clause.Expression);
        return found;
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

            if (list is not null && FieldIndex.Ready(list, name) is { Column: { } column })
            {
                return (Expression.Property(Item, column), field.Type.ValueKind, null);
            }

            if (list is not null && FieldIndex.Ready(list, name) is { Kind: IndexKind.Values })
            {
                throw Unsupported($"Field '{name}' can be filtered with eq, ne and in, but not sorted.");
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

        if (operand.Kind == FieldValueKind.Boolean && operand.Expression.Type == typeof(string))
        {
            // Booleans in a text column are stored as "true" / "false".
            return Expression.Constant(value is bool text ? text ? "true" : "false" : throw Unsupported("true or false is expected."), typeof(string));
        }

        if (operand.Expression.Type == typeof(bool))
        {
            return Expression.Constant(value is bool flag ? flag : throw Unsupported("true or false is expected."));
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

    /// <summary>Removes conversions and resolves parameter aliases (<c>@me</c>, <c>@today</c>, …) to their values.</summary>
    private QueryNode Unwrap(QueryNode node) => node switch
    {
        ConvertNode convert => Unwrap(convert.Source),
        ParameterAliasNode alias when aliases is not null && aliases.TryGetValue(alias.Alias, out var value) && value is not null => Unwrap(value),
        ParameterAliasNode alias => throw Unsupported($"Unknown parameter '{alias.Alias}'."),
        _ => node,
    };

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

/// <summary>
/// JSON field expressions shared with smart folders, so grouping does not build its own dialect.
/// Equality of a stored text value (not containment): a group key compared with a path segment.
/// </summary>
internal static class ItemFields
{
    private static readonly ParameterExpression Item = Expression.Parameter(typeof(ListItem), "i");
    private static readonly Expression Document = Expression.Property(Item, nameof(ListItem.Fields));

    /// <summary>The field's stored text, or its year (<c>yyyy</c>) or month (<c>yyyy-MM</c>) prefix.</summary>
    public static Expression<Func<ListItem, string?>> GroupKey(string field, string? by)
    {
        Expression text = Expression.Call(ItemQueryTranslator.JsonText, Document, Expression.Constant(field));
        if (by is "year" or "month")
        {
            text = Expression.Call(text, ItemQueryTranslator.StringSubstring, Expression.Constant(0), Expression.Constant(by == "year" ? 4 : 7));
        }

        return Expression.Lambda<Func<ListItem, string?>>(text, Item);
    }

    /// <summary>The group key equals <paramref name="value"/>.</summary>
    public static Expression<Func<ListItem, bool>> GroupEquals(string field, string? by, string value)
    {
        var key = GroupKey(field, by);
        return Expression.Lambda<Func<ListItem, bool>>(Expression.Equal(key.Body, Expression.Constant(value, typeof(string))), key.Parameters);
    }

    /// <summary>The JSON field is absent (the "(empty)" group).</summary>
    public static Expression<Func<ListItem, bool>> Missing(string field) =>
        Expression.Lambda<Func<ListItem, bool>>(
            Expression.Not(Expression.Call(ItemQueryTranslator.JsonHasProperty, Document, Expression.Constant(field))), Item);

    public static Expression<Func<ListItem, bool>> And(Expression<Func<ListItem, bool>> left, Expression<Func<ListItem, bool>> right)
    {
        var body = new ReplaceParameter(right.Parameters[0], left.Parameters[0]).Visit(right.Body)!;
        return Expression.Lambda<Func<ListItem, bool>>(Expression.AndAlso(left.Body, body), left.Parameters);
    }

    internal sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
