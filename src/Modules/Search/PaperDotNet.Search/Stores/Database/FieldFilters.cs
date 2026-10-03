using System.Linq.Expressions;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;

namespace PaperDotNet.Search.Stores.Database;

/// <summary>
/// Turns a <see cref="SearchFilter"/> into a predicate on search documents: each condition is an <c>EXISTS</c> on
/// <see cref="SearchDbContext.FieldValues"/> (indexed by tenant, field, kind and value), so a multi-value field matches
/// when any value does, and <see cref="SearchOperator.NotEqual"/> is the negation of equality. Values become query
/// parameters.
/// </summary>
internal sealed class FieldFilters(SearchDbContext db)
{
    private readonly ParameterExpression _document = Expression.Parameter(typeof(SearchDocument), "d");

    public Expression<Func<SearchDocument, bool>> Predicate(SearchFilter filter) =>
        Expression.Lambda<Func<SearchDocument, bool>>(Body(filter), _document);

    private Expression Body(SearchFilter filter) => filter switch
    {
        SearchAnd all when all.Items.Count > 0 => all.Items.Select(Body).Aggregate(Expression.AndAlso),
        SearchAnd => Expression.Constant(true),
        SearchOr any when any.Items.Count > 0 => any.Items.Select(Body).Aggregate(Expression.OrElse),
        SearchOr => Expression.Constant(false),
        SearchNot not => Expression.Not(Body(not.Item)),
        SearchHasValue has => Exists(has.Field, has.Kind, _ => true),
        SearchIn @in => In(@in),
        SearchCompare { Operator: SearchOperator.NotEqual } compare => Expression.Not(Body(compare with { Operator = SearchOperator.Equal })),
        SearchCompare compare => Compare(compare),
        _ => throw new ArgumentException($"Unknown filter {filter.GetType().Name}.", nameof(filter)),
    };

    private MethodCallExpression In(SearchIn filter)
    {
        if (SearchField.IsText(filter.Kind))
        {
            var texts = filter.Values.Select(v => v.Text).OfType<string>().ToArray();
            return Exists(filter.Field, filter.Kind, v => texts.Contains(v.Text!));
        }

        var numbers = filter.Values.Select(v => v.Number).OfType<double>().ToArray();
        return Exists(filter.Field, filter.Kind, v => v.Number != null && numbers.Contains(v.Number.Value));
    }

    private MethodCallExpression Compare(SearchCompare filter)
    {
        if (SearchField.IsText(filter.Kind))
        {
            if (filter.Operator != SearchOperator.Equal)
            {
                throw new ArgumentException($"Text fields only support equality ({filter.Field}).", nameof(filter));
            }

            var text = filter.Value.Text;
            return Exists(filter.Field, filter.Kind, v => v.Text == text);
        }

        var n = filter.Value.Number;
        return filter.Operator switch
        {
            SearchOperator.Equal => Exists(filter.Field, filter.Kind, v => v.Number == n),
            SearchOperator.GreaterThan => Exists(filter.Field, filter.Kind, v => v.Number > n),
            SearchOperator.GreaterThanOrEqual => Exists(filter.Field, filter.Kind, v => v.Number >= n),
            SearchOperator.LessThan => Exists(filter.Field, filter.Kind, v => v.Number < n),
            SearchOperator.LessThanOrEqual => Exists(filter.Field, filter.Kind, v => v.Number <= n),
            _ => throw new ArgumentException($"Unknown operator {filter.Operator}.", nameof(filter)),
        };
    }

    /// <summary><c>EXISTS (value of the document's field <paramref name="name"/> of <paramref name="kind"/> matching <paramref name="match"/>)</c>.</summary>
    private MethodCallExpression Exists(string name, SearchFieldKind kind, Expression<Func<SearchFieldValue, bool>> match)
    {
        var kindValue = (int)kind;
        Expression<Func<SearchFieldValue, Guid, bool>> field = (v, id) => v.DocumentId == id && v.Name == name && v.Kind == kindValue;
        var value = field.Parameters[0];
        var body = Expression.AndAlso(
            new Replace(field.Parameters[1], Expression.Property(_document, nameof(SearchDocument.Id))).Visit(field.Body),
            new Replace(match.Parameters[0], value).Visit(match.Body));
        var lambda = Expression.Lambda<Func<SearchFieldValue, bool>>(body, value);
        Expression<Func<IQueryable<SearchFieldValue>>> source = () => db.FieldValues;
        return Expression.Call(typeof(Queryable), nameof(Queryable.Any), [typeof(SearchFieldValue)], source.Body, Expression.Quote(lambda));
    }

    private sealed class Replace(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
