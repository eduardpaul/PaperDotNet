#nullable enable // PaperDotNet: upstream tests of the vendored WebDAV library (ADR-0047).
using System;

using FubarDev.WebDavServer.Parsing;

using Xunit;

namespace FubarDev.WebDavServer.Models.Tests;

public class IfConditionTests
{
    [Theory]
    [InlineData(false, "http://localhost/", "<http://localhost/>")]
    [InlineData(true, "http://localhost/", "not <http://localhost/>")]
    [InlineData(true, "http://localhost/", "Not <http://localhost/>")]
    public void TestStateToken(bool not, string stateToken, string input)
    {
        // PaperDotNet: HeaderParser has no condition entry point; parse the condition as a one-condition If list.
        Assert.True(HeaderParser.TryParseIfHeader($"({input})", out var header));
        var condition = Assert.Single(Assert.Single(header.NoTagLists).List);
        Assert.Equal(not, condition.Not);
        Assert.Null(condition.EntityTag);
        Assert.Equal(new Uri(stateToken), condition.StateToken);
    }

    [Theory]
    [InlineData(false, false, "foo", "[\"foo\"]")]
    [InlineData(true, false, "foo", "not [\"foo\"]")]
    [InlineData(true, false, "foo", "Not [\"foo\"]")]
    [InlineData(true, false, "foo", "Not [ \"foo\" ]")]
    [InlineData(false, true, "foo", "[w/\"foo\"]")]
    [InlineData(true, true, "foo", "Not [w/\"foo\"]")]
    public void TestEntityTag(bool not, bool weak, string value, string input)
    {
        // PaperDotNet: HeaderParser has no condition entry point; parse the condition as a one-condition If list.
        Assert.True(HeaderParser.TryParseIfHeader($"({input})", out var header));
        var condition = Assert.Single(Assert.Single(header.NoTagLists).List);
        Assert.Equal(not, condition.Not);
        Assert.Null(condition.StateToken);
        Assert.NotNull(condition.EntityTag);
        Assert.Equal(new EntityTag(weak, value), condition.EntityTag!.Value, EntityTagComparer.Weak);
    }
}
