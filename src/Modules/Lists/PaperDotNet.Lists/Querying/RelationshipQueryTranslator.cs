using System.Globalization;
using System.Linq.Expressions;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using PaperDotNet.Lists.Data;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists.Querying;

/// <summary>Bounded OData scalar filters over open relationship attributes, translated entirely by EF.</summary>
internal static class RelationshipQueryTranslator
{
    public static Expression<Func<ItemRelation, bool>> Parse(string filter)
    {
        if (filter.Length > 4096) throw new ArgumentException("Relationship filters allow at most 4096 characters.");
        var model = new EdmModel();
        var attributes = new EdmComplexType("PaperDotNet", "RelationshipAttributes", null, false, true);
        model.AddElement(attributes);
        var edge = new EdmEntityType("PaperDotNet", "Relationship");
        var id = edge.AddStructuralProperty("id", EdmPrimitiveTypeKind.Guid);
        edge.AddKeys(id);
        edge.AddStructuralProperty("attributes", new EdmComplexTypeReference(attributes, false));
        edge.AddStructuralProperty("directed", EdmPrimitiveTypeKind.Boolean);
        model.AddElement(edge);
        var container = new EdmEntityContainer("PaperDotNet", "Relationships");
        model.AddElement(container);
        var set = container.AddEntitySet("relationships", edge);
        try
        {
            var parser = new ODataQueryOptionParser(model, edge, set, new Dictionary<string, string> { ["$filter"] = filter });
            var clause = parser.ParseFilter();
            var parameter = Expression.Parameter(typeof(ItemRelation), "r");
            return Expression.Lambda<Func<ItemRelation, bool>>(Translate(clause.Expression, parameter), parameter);
        }
        catch (ODataException ex) { throw new ArgumentException(ex.Message); }
    }

    private static SingleValueNode Unwrap(SingleValueNode node) => node is ConvertNode convert ? Unwrap(convert.Source) : node;

    private static Expression Translate(SingleValueNode node, ParameterExpression parameter)
    {
        node = Unwrap(node);
        if (node is UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } unary) return Expression.Not(Translate(unary.Operand, parameter));
        if (node is ConstantNode { Value: bool boolean }) return Expression.Constant(boolean);
        if (node is not BinaryOperatorNode binary) throw new ArgumentException("Use scalar comparisons and and/or/not in relationship filters.");
        if (binary.OperatorKind is BinaryOperatorKind.And or BinaryOperatorKind.Or)
        {
            var left = Translate(binary.Left, parameter);
            var right = Translate(binary.Right, parameter);
            return binary.OperatorKind == BinaryOperatorKind.And ? Expression.AndAlso(left, right) : Expression.OrElse(left, right);
        }
        var property = Unwrap(binary.Left);
        var literal = Unwrap(binary.Right) as ConstantNode;
        var op = binary.OperatorKind;
        if (literal is null && Unwrap(binary.Left) is ConstantNode constant)
        {
            property = Unwrap(binary.Right);
            literal = constant;
            op = op switch
            {
                BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
                BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
                BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
                BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
                _ => op
            };
        }
        if (literal is null) throw new ArgumentException("Compare a relationship attribute with a scalar literal.");
        Expression value;
        Expression expected;
        Expression? present = null;
        if (property is SingleValueOpenPropertyAccessNode access && access.Source is SingleComplexNode { Property.Name: "attributes" })
        {
            var document = Expression.Property(parameter, nameof(ItemRelation.Attributes));
            var key = Expression.Constant(access.Name);
            if (literal.Value is null)
            {
                var exists = Expression.Call(typeof(JsonFunctions), nameof(JsonFunctions.HasProperty), null, document, key);
                return op switch
                {
                    BinaryOperatorKind.Equal => Expression.Not(exists),
                    BinaryOperatorKind.NotEqual => exists,
                    _ => throw new ArgumentException("Null supports only eq/ne.")
                };
            }
            var (method, type, converted) = literal.Value switch
            {
                string text => (nameof(JsonFunctions.ScalarText), typeof(string), (object)text),
                bool flag => (nameof(JsonFunctions.ScalarBoolean), typeof(bool?), (object)flag),
                int or long or float or double or decimal => (nameof(JsonFunctions.ScalarNumber), typeof(double?), (object)Convert.ToDouble(literal.Value, CultureInfo.InvariantCulture)),
                _ => throw new ArgumentException("Attributes support string, number and boolean literals.")
            };
            value = Expression.Call(typeof(JsonFunctions), method, null, document, key);
            expected = Expression.Convert(Expression.Constant(converted), type);
            present = Expression.NotEqual(value, Expression.Constant(null, type));
        }
        else if (property is SingleValuePropertyAccessNode top && top.Property.Name is "id" or "directed")
        {
            value = Expression.Property(parameter, top.Property.Name == "id" ? nameof(ItemRelation.Id) : nameof(ItemRelation.Directed));
            if (literal.Value?.GetType() != value.Type) throw new ArgumentException("The literal does not match the relationship property type.");
            expected = Expression.Constant(literal.Value, value.Type);
        }
        else throw new ArgumentException("Filter attributes/key, id or directed; nested attributes are not supported.");
        if (op is not (BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual) && value.Type != typeof(double?))
            throw new ArgumentException("Ordering comparisons require numeric attributes.");
        var comparison = op switch
        {
            BinaryOperatorKind.Equal => Expression.Equal(value, expected),
            BinaryOperatorKind.NotEqual => Expression.NotEqual(value, expected),
            BinaryOperatorKind.LessThan => Expression.LessThan(value, expected),
            BinaryOperatorKind.LessThanOrEqual => Expression.LessThanOrEqual(value, expected),
            BinaryOperatorKind.GreaterThan => Expression.GreaterThan(value, expected),
            BinaryOperatorKind.GreaterThanOrEqual => Expression.GreaterThanOrEqual(value, expected),
            _ => throw new ArgumentException("Unsupported relationship comparison.")
        };
        return present is null ? comparison : Expression.AndAlso(present, comparison);
    }
}
