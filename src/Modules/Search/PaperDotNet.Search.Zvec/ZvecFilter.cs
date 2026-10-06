using System.Globalization;
using System.Text.RegularExpressions;
using PaperDotNet.Search.Contracts;

namespace PaperDotNet.Search.Zvec;

/// <summary>
/// Builds zvec filter expressions, the only place filter text is made. Literals are ids (hex), our own words and
/// encoded field tokens, checked against <see cref="SafeLiteral"/>; numbers use the invariant culture without exponent.
/// zvec's grammar has no general <c>NOT</c>, so negations are pushed down to the conditions (<c>NOT CONTAIN_ANY</c>,
/// inverted comparisons).
/// </summary>
internal static partial class ZvecFilter
{
    public const string True = $"{ZvecLayout.Kind} IS NOT NULL";
    public const string False = $"{ZvecLayout.Kind} IS NULL";

    [GeneratedRegex("^[A-Za-z0-9_:.-]*$")]
    private static partial Regex SafeLiteral();

    public static string Quote(string literal) =>
        SafeLiteral().IsMatch(literal) ? $"'{literal}'" : throw new ArgumentException($"Unsafe filter literal '{literal}'.", nameof(literal));

    public static string Number(double value) => value.ToString("0.0###############", CultureInfo.InvariantCulture);

    public static string Equal(string column, string literal) => $"{column} = {Quote(literal)}";

    public static string In(string column, IEnumerable<string> literals)
    {
        var list = literals.Distinct(StringComparer.Ordinal).Select(Quote).ToList();
        return list.Count == 0 ? False : $"{column} IN ({string.Join(", ", list)})";
    }

    public static string ContainsAny(string column, IEnumerable<string> literals, bool negate = false)
    {
        var list = literals.Distinct(StringComparer.Ordinal).Select(Quote).ToList();
        return list.Count == 0
            ? negate ? True : False
            : $"{column} {(negate ? "NOT " : string.Empty)}CONTAIN_ANY ({string.Join(", ", list)})";
    }

    public static string And(IEnumerable<string> conditions)
    {
        var list = conditions.Where(c => c != True).ToList();
        return list.Contains(False) ? False : list.Count switch
        {
            0 => True,
            1 => list[0],
            _ => string.Join(" AND ", list.Select(c => $"({c})")),
        };
    }

    public static string Or(IEnumerable<string> conditions)
    {
        var list = conditions.Where(c => c != False).ToList();
        return list.Contains(True) ? True : list.Count switch
        {
            0 => False,
            1 => list[0],
            _ => string.Join(" OR ", list.Select(c => $"({c})")),
        };
    }

    /// <summary>The conditions of a search (without the row kind).</summary>
    public static string For(StoreFilter filter, ZvecCatalog catalog)
    {
        var conditions = new List<string>
        {
            In(ZvecLayout.ScopeId, filter.ReadableScopes.Select(ZvecLayout.Id)),
        };
        if (filter.ReadableItems is { } items)
        {
            conditions.Add(Or([$"{ZvecLayout.SourceType} != {Quote("listItem")}", In(ZvecLayout.DocumentId, items.Select(ZvecLayout.Id))]));
        }
        foreach (var excluded in filter.ExcludedContainers)
        {
            conditions.Add($"{ZvecLayout.ContainerId} != {Quote(ZvecLayout.Id(excluded))}");
        }
        if (filter.WorkspaceId is { } ws)
        {
            conditions.Add(Equal(ZvecLayout.WorkspaceId, ZvecLayout.Id(ws)));
        }

        if (filter.ContainerId is { } container)
        {
            conditions.Add(Equal(ZvecLayout.ContainerId, ZvecLayout.Id(container)));
        }

        if (filter.ContentTypeId is { } contentType)
        {
            conditions.Add(Equal(ZvecLayout.ContentTypeId, ZvecLayout.Id(contentType)));
        }

        if (filter.CreatedBy is { } author)
        {
            conditions.Add(Equal(ZvecLayout.CreatedBy, ZvecLayout.Id(author)));
        }

        if (filter.UpdatedFrom is { } from)
        {
            conditions.Add($"{ZvecLayout.UpdatedAt} >= {from.UtcTicks.ToString(CultureInfo.InvariantCulture)}");
        }

        if (filter.UpdatedTo is { } to)
        {
            conditions.Add($"{ZvecLayout.UpdatedAt} < {to.UtcTicks.ToString(CultureInfo.InvariantCulture)}");
        }

        if (filter.TermIds is { Count: > 0 } terms)
        {
            conditions.Add(ContainsAny(ZvecLayout.TermIds, terms.Select(ZvecLayout.Id)));
        }

        if (filter.Fields is { } fields)
        {
            conditions.Add(Fields(fields, catalog, negate: false));
        }

        return And(conditions);
    }

    /// <summary>A field condition; with <paramref name="negate"/> its negation, pushed down to the leaves.</summary>
    public static string Fields(SearchFilter filter, ZvecCatalog catalog, bool negate)
    {
        switch (filter)
        {
            case SearchAnd all:
                return negate ? Or(all.Items.Select(i => Fields(i, catalog, true))) : And(all.Items.Select(i => Fields(i, catalog, false)));
            case SearchOr any:
                return negate ? And(any.Items.Select(i => Fields(i, catalog, true))) : Or(any.Items.Select(i => Fields(i, catalog, false)));
            case SearchNot not:
                return Fields(not.Item, catalog, !negate);
            case SearchHasValue has:
                return ContainsAny(ZvecLayout.FieldNames, [ZvecLayout.FieldKey(has.Field, has.Kind)], negate);
            case SearchCompare { Operator: SearchOperator.NotEqual } compare:
                return Fields(compare with { Operator = SearchOperator.Equal }, catalog, !negate);
            case SearchCompare compare when IsToken(compare.Kind):
                if (compare.Operator != SearchOperator.Equal)
                {
                    throw new ArgumentException($"{compare.Field} supports equality only.", nameof(filter));
                }

                return ContainsAny(ZvecLayout.FieldTokens, [ZvecLayout.Token(ZvecLayout.FieldKey(compare.Field, compare.Kind), compare.Kind, compare.Value)], negate);
            case SearchIn @in when IsToken(@in.Kind):
                var key = ZvecLayout.FieldKey(@in.Field, @in.Kind);
                return ContainsAny(ZvecLayout.FieldTokens, @in.Values.Select(v => ZvecLayout.Token(key, @in.Kind, v)), negate);
            case SearchCompare compare:
                return NumberCondition(compare.Field, compare.Kind, catalog, negate, column =>
                    $"{column} {Operator(negate ? Inverse(compare.Operator) : compare.Operator)} {Number(compare.Value.Number ?? 0)}");
            case SearchIn @in:
                var numbers = @in.Values.Select(v => v.Number).OfType<double>().Distinct().Select(Number).ToList();
                return numbers.Count == 0
                    ? negate ? True : False
                    : NumberCondition(@in.Field, @in.Kind, catalog, negate, column => $"{column} {(negate ? "NOT " : string.Empty)}IN ({string.Join(", ", numbers)})");
            default:
                throw new ArgumentException($"Unknown filter {filter.GetType().Name}.", nameof(filter));
        }
    }

    private static bool IsToken(SearchFieldKind kind) => SearchField.IsText(kind) || kind == SearchFieldKind.Boolean;

    /// <summary>
    /// A comparison on a number column. Documents without the field never match it; its negation matches them (any
    /// semantics, like the database store). A field that was never indexed has no column.
    /// </summary>
    private static string NumberCondition(string field, SearchFieldKind kind, ZvecCatalog catalog, bool negate, Func<string, string> comparison)
    {
        var key = ZvecLayout.FieldKey(field, kind);
        if (!catalog.HasNumberColumn(key))
        {
            return negate ? True : False;
        }

        var column = ZvecLayout.NumberColumn(key);
        return negate
            ? Or([ContainsAny(ZvecLayout.FieldNames, [key], negate: true), comparison(column)])
            : comparison(column);
    }

    private static SearchOperator Inverse(SearchOperator op) => op switch
    {
        SearchOperator.Equal => SearchOperator.NotEqual,
        SearchOperator.NotEqual => SearchOperator.Equal,
        SearchOperator.GreaterThan => SearchOperator.LessThanOrEqual,
        SearchOperator.GreaterThanOrEqual => SearchOperator.LessThan,
        SearchOperator.LessThan => SearchOperator.GreaterThanOrEqual,
        _ => SearchOperator.GreaterThan,
    };

    private static string Operator(SearchOperator op) => op switch
    {
        SearchOperator.Equal => "=",
        SearchOperator.NotEqual => "!=",
        SearchOperator.GreaterThan => ">",
        SearchOperator.GreaterThanOrEqual => ">=",
        SearchOperator.LessThan => "<",
        _ => "<=",
    };
}
