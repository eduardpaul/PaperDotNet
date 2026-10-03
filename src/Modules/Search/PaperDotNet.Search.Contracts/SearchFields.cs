using System.Globalization;

namespace PaperDotNet.Search.Contracts;

/// <summary>
/// The kind of a search field (ADR-0043): how its values are stored and compared. A field name with one kind is one
/// search field across all lists, like a managed property.
/// </summary>
public enum SearchFieldKind
{
    /// <summary>Exact text values (text, choice, email, url fields): equality and lists of values.</summary>
    Keyword,

    /// <summary>Numbers (number, currency fields): equality and ranges.</summary>
    Number,

    /// <summary>Dates without time: equality and ranges.</summary>
    Date,

    /// <summary>Points in time: equality and ranges.</summary>
    DateTime,

    Boolean,

    /// <summary>Ids of users or items (person, lookup fields): equality and lists of values.</summary>
    Reference,

    /// <summary>Term ids (managed metadata, keywords fields): equality and lists of values.</summary>
    Terms,
}

/// <summary>
/// One value of a search field, in the form every store can keep: <see cref="Text"/> for keyword, reference and term
/// fields (ids as lowercase <c>D</c> format), <see cref="Number"/> for the others (dates as days since 1970-01-01,
/// points in time as Unix milliseconds, booleans as 0 or 1).
/// </summary>
public readonly record struct SearchValue(string? Text, double? Number)
{
    public static SearchValue Of(string text) => new(text, null);

    public static SearchValue Of(Guid id) => new(id.ToString("D", CultureInfo.InvariantCulture), null);

    public static SearchValue Of(double number) => new(null, number);

    public static SearchValue Of(bool value) => new(null, value ? 1 : 0);

    public static SearchValue Of(DateOnly date) => new(null, date.DayNumber - DateOnly.FromDateTime(System.DateTime.UnixEpoch).DayNumber);

    public static SearchValue Of(DateTimeOffset time) => new(null, time.ToUnixTimeMilliseconds());

    public override string ToString() => Text ?? Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>A field of a search document with its values (several for multi-value fields); filterable and queryable.</summary>
public sealed record SearchField(string Name, SearchFieldKind Kind, IReadOnlyList<SearchValue> Values)
{
    /// <summary>Longest text value kept; longer values are not filterable.</summary>
    public const int MaxTextLength = 256;

    /// <summary>The field asks for a fast filter index (ADR-0035 <c>indexed: true</c>); stores may index every field.</summary>
    public bool Indexed { get; init; }

    /// <summary>Whether values of <paramref name="kind"/> are text (compared by equality only).</summary>
    public static bool IsText(SearchFieldKind kind) => kind is SearchFieldKind.Keyword or SearchFieldKind.Reference or SearchFieldKind.Terms;
}

/// <summary>A search field that has values in the index, for <c>$filter</c>.</summary>
public sealed record SearchFieldInfo(string Name, SearchFieldKind Kind);

public enum SearchOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
}

/// <summary>
/// A condition on search fields, the store-neutral form of <c>$filter</c> (ADR-0043). A multi-value field matches when
/// any of its values matches; <see cref="SearchOperator.NotEqual"/> matches when none does, including documents without
/// the field. Text kinds support equality only.
/// </summary>
public abstract record SearchFilter;

public sealed record SearchCompare(string Field, SearchFieldKind Kind, SearchOperator Operator, SearchValue Value) : SearchFilter;

/// <summary>Any value of the field is one of <see cref="Values"/>.</summary>
public sealed record SearchIn(string Field, SearchFieldKind Kind, IReadOnlyList<SearchValue> Values) : SearchFilter;

/// <summary>The document has a value for the field.</summary>
public sealed record SearchHasValue(string Field, SearchFieldKind Kind) : SearchFilter;

public sealed record SearchAnd(IReadOnlyList<SearchFilter> Items) : SearchFilter;

public sealed record SearchOr(IReadOnlyList<SearchFilter> Items) : SearchFilter;

public sealed record SearchNot(SearchFilter Item) : SearchFilter;
