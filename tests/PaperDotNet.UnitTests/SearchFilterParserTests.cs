using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Features;

namespace PaperDotNet.UnitTests;

/// <summary><c>$filter</c> of <c>/v1.0/search</c> to the store-neutral filter tree (ADR-0043).</summary>
public sealed class SearchFilterParserTests
{
    private static readonly Guid Alice = Guid.Parse("8d1c3a4e-0000-4000-8000-000000000001");

    private static readonly SearchFieldInfo[] Fields =
    [
        new("total", SearchFieldKind.Number),
        new("status", SearchFieldKind.Keyword),
        new("paid", SearchFieldKind.Boolean),
        new("due", SearchFieldKind.Date),
        new("sent", SearchFieldKind.DateTime),
        new("owner", SearchFieldKind.Reference),
        new("code", SearchFieldKind.Keyword),
        new("code", SearchFieldKind.Number),
    ];

    private static SearchFilter Parse(string filter) => SearchFilterParser.Parse(filter, Fields);

    [Fact]
    public void Comparisons_become_typed_conditions()
    {
        Assert.Equal(new SearchCompare("total", SearchFieldKind.Number, SearchOperator.GreaterThan, SearchValue.Of(100)), Parse("fields/total gt 100"));
        Assert.Equal(new SearchCompare("total", SearchFieldKind.Number, SearchOperator.LessThan, SearchValue.Of(9.5)), Parse("9.5 gt fields/total"));
        Assert.Equal(new SearchCompare("status", SearchFieldKind.Keyword, SearchOperator.NotEqual, SearchValue.Of("o'k")), Parse("fields/status ne 'o''k'"));
        Assert.Equal(new SearchCompare("paid", SearchFieldKind.Boolean, SearchOperator.Equal, SearchValue.Of(true)), Parse("fields/paid eq true"));
        Assert.Equal(new SearchCompare("due", SearchFieldKind.Date, SearchOperator.GreaterThanOrEqual, SearchValue.Of(new DateOnly(2026, 3, 1))), Parse("fields/due ge 2026-03-01"));
        Assert.Equal(
            new SearchCompare("sent", SearchFieldKind.DateTime, SearchOperator.LessThanOrEqual, SearchValue.Of(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero))),
            Parse("fields/sent le 2026-03-01T10:00:00Z"));
        Assert.Equal(new SearchCompare("owner", SearchFieldKind.Reference, SearchOperator.Equal, SearchValue.Of(Alice)), Parse($"fields/owner eq {Alice}"));
    }

    [Fact]
    public void Lists_null_and_logic_are_translated()
    {
        var parsed = Parse("fields/status in ('open', 'late') and (fields/total eq null or not (fields/paid eq true)) and fields/owner ne null");

        var and = Assert.IsType<SearchAnd>(parsed);
        Assert.Equal(3, and.Items.Count);
        var @in = Assert.IsType<SearchIn>(and.Items[0]);
        Assert.Equal([SearchValue.Of("open"), SearchValue.Of("late")], @in.Values);
        var or = Assert.IsType<SearchOr>(and.Items[1]);
        Assert.Equal(new SearchNot(new SearchHasValue("total", SearchFieldKind.Number)), or.Items[0]);
        Assert.IsType<SearchNot>(or.Items[1]);
        Assert.Equal(new SearchHasValue("owner", SearchFieldKind.Reference), and.Items[2]);
    }

    [Fact]
    public void Names_shared_by_several_kinds_get_the_kind_as_suffix()
    {
        Assert.Equal(new SearchCompare("code", SearchFieldKind.Number, SearchOperator.Equal, SearchValue.Of(7)), Parse("fields/code_number eq 7"));
        Assert.Equal(new SearchCompare("code", SearchFieldKind.Keyword, SearchOperator.Equal, SearchValue.Of("x")), Parse("fields/code_keyword eq 'x'"));
        Assert.Throws<ArgumentException>(() => Parse("fields/code eq 'x'"));
    }

    [Theory]
    [InlineData("fields/unknown eq 1")]
    [InlineData("fields/status gt 'a'")]
    [InlineData("fields/paid lt true")]
    [InlineData("fields/total eq 'many'")]
    [InlineData("fields/total gt null")]
    [InlineData("fields/total add 1 eq 2")]
    [InlineData("contains(fields/status, 'o')")]
    [InlineData("id eq 8d1c3a4e-0000-4000-8000-000000000001")]
    [InlineData("fields/total gt")]
    public void Unsupported_filters_are_rejected(string filter) => Assert.Throws<ArgumentException>(() => Parse(filter));

    [Fact]
    public void Long_filters_are_rejected() =>
        Assert.Throws<ArgumentException>(() => Parse(string.Join(" or ", Enumerable.Repeat("fields/total eq 1", 300))));
}
