using PaperDotNet.Persistence;
using PaperDotNet.Persistence.PostgreSql;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.UnitTests;

public sealed class FullTextQueryTests
{
    private static FullTextQuery Parse(string text) => FullTextQuery.Parse(text, out _)!;

    [Fact]
    public void Words_phrases_or_not_and_prefixes_are_parsed()
    {
        var query = Parse("Invoice \"due date\" paid OR open -draft NOT spam acc*");

        Assert.Equal(4, query.Groups.Count);
        Assert.Equal(["invoice"], query.Groups[0][0].Tokens);
        Assert.Equal(["due", "date"], query.Groups[1][0].Tokens);
        Assert.Equal(2, query.Groups[2].Count);
        Assert.True(query.Groups[3][0].Prefix);
        Assert.Equal(["draft", "spam"], query.Excluded.Select(t => t.Tokens[0]));
    }

    [Fact]
    public void Punctuation_splits_words_into_phrases()
    {
        var query = Parse("INV-2026");

        Assert.Equal(["inv", "2026"], query.Groups[0][0].Tokens);
    }

    [Fact]
    public void Only_exclusions_or_no_words_are_rejected()
    {
        Assert.Null(FullTextQuery.Parse("-draft", out var error));
        Assert.NotNull(error);
        Assert.Null(FullTextQuery.Parse("  !!! ", out _));
    }

    [Fact]
    public void Queries_render_for_both_providers()
    {
        var query = Parse("invoice \"due date\" paid OR open -draft acc*");

        Assert.Equal("'invoice' & ('due' <-> 'date') & ('paid' | 'open') & 'acc':* & !'draft'", PostgreSqlFullTextSearch.Render(query));
        Assert.Equal("(\"invoice\" AND \"due date\" AND (\"paid\" OR \"open\") AND \"acc\"*) NOT (\"draft\")", SqliteFullTextSearch.Render(query));
    }

    [Fact]
    public void Language_aware_queries_expand_every_term_before_combining()
    {
        var (sql, parameters) = PostgreSqlFullTextSearch.RenderWithLanguages(Parse("chairs OR desk -meeting"));

        Assert.Equal(["'chairs'", "'desk'", "'meeting'"], parameters);
        Assert.StartsWith("((to_tsquery('simple', {0}) || to_tsquery('danish', {0})", sql, StringComparison.Ordinal);
        Assert.Contains(") || (to_tsquery('simple', {1})", sql, StringComparison.Ordinal);
        Assert.Contains(" && !!(to_tsquery('simple', {2})", sql, StringComparison.Ordinal);
        Assert.Contains("to_tsquery('english', {2})", sql, StringComparison.Ordinal);
    }
}
