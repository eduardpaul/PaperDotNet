using Microsoft.AspNetCore.Http;
using PaperDotNet.Api;

namespace PaperDotNet.UnitTests;

public sealed class ETagTests
{
    [Theory]
    [InlineData("\"42\"", 42u)]
    [InlineData("W/\"7\"", 7u)]
    public void If_match_is_parsed(string header, uint expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = header;

        Assert.True(ETags.TryGetIfMatch(context.Request, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("42")]
    [InlineData("\"abc\"")]
    public void Invalid_if_match_is_rejected(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = header;

        Assert.False(ETags.TryGetIfMatch(context.Request, out _));
    }

    [Fact]
    public void ETag_is_a_quoted_version() => Assert.Equal("\"12\"", ETags.From(12));
}
