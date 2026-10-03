using PaperDotNet.Abstractions;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Zvec;

namespace PaperDotNet.UnitTests;

/// <summary>Filter and query text of the zvec store (ADR-0044): the only places zvec syntax is written.</summary>
public sealed class ZvecBuilderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pdn_zvec_").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ZvecCatalog Catalog(params SearchField[] fields)
    {
        var catalog = ZvecCatalog.Load(Path.Combine(_directory, "c"));
        catalog.Register(fields, _ => { });
        return catalog;
    }

    [Theory]
    [InlineData("O'Brien")]
    [InlineData("back\\slash")]
    [InlineData("a b")]
    [InlineData("x') OR (1")]
    [InlineData("\"quoted\"")]
    public void Unsafe_literals_are_refused(string literal) => Assert.Throws<ArgumentException>(() => ZvecFilter.Quote(literal));

    [Fact]
    public void Numbers_are_written_without_culture_or_exponent()
    {
        Assert.Equal("120.5", ZvecFilter.Number(120.5));
        Assert.Equal("-0.25", ZvecFilter.Number(-0.25));
        Assert.Equal("100000000000000000000.0", ZvecFilter.Number(1e20));
        Assert.Equal("7.0", ZvecFilter.Number(7));
    }

    [Fact]
    public void Constants_simplify_and_empty_lists_match_nothing()
    {
        Assert.Equal(ZvecFilter.False, ZvecFilter.In("scope_id", []));
        Assert.Equal(ZvecFilter.False, ZvecFilter.And(["a = 'b'", ZvecFilter.False]));
        Assert.Equal("a = 'b'", ZvecFilter.And(["a = 'b'", ZvecFilter.True]));
        Assert.Equal(ZvecFilter.True, ZvecFilter.Or(["a = 'b'", ZvecFilter.True]));
        Assert.Equal("(a = 'b') OR (c = 'd')", ZvecFilter.Or(["a = 'b'", ZvecFilter.False, "c = 'd'"]));
    }

    [Fact]
    public void Store_filters_use_ids_ticks_and_terms()
    {
        var (scope, ws, term) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var filter = ZvecFilter.For(new StoreFilter([scope]) { WorkspaceId = ws, TermIds = [term], UpdatedFrom = from }, Catalog());

        Assert.Equal(
            $"(scope_id IN ('{scope:N}')) AND (workspace_id = '{ws:N}') AND (updated_at >= {from.UtcTicks}) AND (term_ids CONTAIN_ANY ('{term:N}'))",
            filter);
    }

    [Fact]
    public void Field_values_are_tokens_so_user_text_never_reaches_the_filter()
    {
        var value = SearchValue.Of("O'Brien \\ \"Co\" ) OR (x");
        var filter = ZvecFilter.Fields(new SearchCompare("vendor", SearchFieldKind.Keyword, SearchOperator.Equal, value), Catalog(), negate: false);

        var key = ZvecLayout.FieldKey("vendor", SearchFieldKind.Keyword);
        Assert.Equal($"field_tokens CONTAIN_ANY ('{ZvecLayout.Token(key, SearchFieldKind.Keyword, value)}')", filter);
        Assert.DoesNotContain("Brien", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Negations_are_pushed_down_to_the_conditions()
    {
        var total = new SearchField("total", SearchFieldKind.Number, [SearchValue.Of(1)]);
        var catalog = Catalog(total);
        var totalKey = ZvecLayout.FieldKey("total", SearchFieldKind.Number);
        var statusKey = ZvecLayout.FieldKey("status", SearchFieldKind.Keyword);

        var filter = ZvecFilter.Fields(new SearchNot(new SearchAnd(
        [
            new SearchCompare("status", SearchFieldKind.Keyword, SearchOperator.Equal, SearchValue.Of("open")),
            new SearchCompare("total", SearchFieldKind.Number, SearchOperator.GreaterThan, SearchValue.Of(100)),
        ])), catalog, negate: false);

        Assert.Equal(
            $"(field_tokens NOT CONTAIN_ANY ('{statusKey}:b3Blbg')) OR ((field_names NOT CONTAIN_ANY ('{totalKey}')) OR (n_{totalKey} <= 100.0))",
            filter);
        Assert.DoesNotContain("NOT (", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Numeric_fields_without_a_column_match_nothing_and_their_negation_everything()
    {
        var unknown = new SearchCompare("missing", SearchFieldKind.Number, SearchOperator.Equal, SearchValue.Of(1));

        Assert.Equal(ZvecFilter.False, ZvecFilter.Fields(unknown, Catalog(), negate: false));
        Assert.Equal(ZvecFilter.True, ZvecFilter.Fields(new SearchNot(unknown), Catalog(), negate: false));
    }

    [Fact]
    public void Queries_become_zvec_full_text_syntax()
    {
        var query = ZvecFullText.From(FullTextQuery.Parse("invoice \"due date\" tax OR levy -draft", out _)!);

        Assert.Equal("invoice AND \"due date\" AND (tax OR levy) AND NOT draft", query.Text);
        Assert.Equal("draft", query.Excluded);
        Assert.Empty(query.Prefixes);
    }

    [Fact]
    public void Prefix_groups_become_trigram_conditions()
    {
        var query = ZvecFullText.From(FullTextQuery.Parse("print* report", out _)!);

        Assert.Equal("report", query.Text);
        Assert.Equal(["pri AND rin AND int"], query.Prefixes);

        // Inside OR groups, shorter than three letters, or without the trigram column, they are whole words.
        Assert.Equal("(print OR scan)", ZvecFullText.From(FullTextQuery.Parse("print* OR scan", out _)!).Text);
        Assert.Equal("ab", ZvecFullText.From(FullTextQuery.Parse("ab*", out _)!).Text);
        Assert.Equal("print", ZvecFullText.From(FullTextQuery.Parse("print*", out _)!, trigrams: false).Text);
        Assert.Null(ZvecFullText.From(FullTextQuery.Parse("print*", out _)!).Text);
    }
}
