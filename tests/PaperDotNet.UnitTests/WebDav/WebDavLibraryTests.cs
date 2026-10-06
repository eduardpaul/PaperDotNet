using FubarDev.WebDavServer;
using FubarDev.WebDavServer.Models;
using FubarDev.WebDavServer.Parsing;
using Microsoft.AspNetCore.Http;

namespace PaperDotNet.UnitTests.WebDav;

/// <summary>Our changes to the vendored WebDAV library (ADR-0047, src/BuildingBlocks/PaperDotNet.WebDav/README.md).</summary>
public sealed class WebDavLibraryTests
{
    [Fact]
    public void Timeout_accepts_the_list_Windows_sends_and_values_above_int32()
    {
        var timeout = TimeoutHeader.Parse(["Infinite, Second-4100000000"]);

        Assert.Equal([TimeoutHeader.Infinite, TimeSpan.FromSeconds(4100000000)], timeout.Values);
    }

    [Theory]
    [InlineData("Second-60, Minute-5")]
    [InlineData("second-60")]
    public void Timeout_ignores_unknown_units_and_case(string value)
    {
        Assert.Equal([TimeSpan.FromSeconds(60)], TimeoutHeader.Parse([value]).Values);
    }

    [Fact]
    public void Malformed_conditional_and_timeout_headers_are_ignored()
    {
        var headers = new WebDavRequestHeaders(new HeaderDictionary
        {
            ["If-None-Match"] = "\"\"etag\"\"",
            ["If-Match"] = "not-an-etag",
            ["If-Modified-Since"] = "yesterday",
            ["Timeout"] = "Fortnight",
        });

        Assert.Null(headers.IfNoneMatch);
        Assert.Null(headers.IfMatch);
        Assert.Null(headers.IfModifiedSince);
    }

    [Fact]
    public void A_malformed_depth_is_a_bad_request()
    {
        var ex = Assert.Throws<WebDavException>(() => new WebDavRequestHeaders(new HeaderDictionary { ["Depth"] = "2" }));

        Assert.Equal(WebDavStatusCode.BadRequest, ex.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(")]
    [InlineData("()")]
    [InlineData("( <urn:a> ")]
    [InlineData("</a>")]
    [InlineData("(<urn:a>) </b> (<urn:a>)")]
    [InlineData("( [\"etag\" )")]
    [InlineData("( not )")]
    [InlineData("( <urn:a> ) garbage")]
    public void Invalid_if_headers_are_rejected(string value)
    {
        Assert.False(HeaderParser.TryParseIfHeader(value, out _));
    }

    [Fact]
    public void A_tagged_if_header_keeps_absolute_resource_urls()
    {
        Assert.True(HeaderParser.TryParseIfHeader(
            "<http://host/dav/a.pdf> (<urn:uuid:181d4fae-7d8c-11d0-a765-00a0c91e6bf2> [\"etag\"])", out var header));

        var tagged = Assert.Single(header.TaggedLists);
        Assert.Equal(new Uri("http://host/dav/a.pdf"), tagged.ResourceTag);
        var conditions = Assert.Single(tagged.Lists);
        Assert.Equal(new Uri("urn:uuid:181d4fae-7d8c-11d0-a765-00a0c91e6bf2"), conditions[0].StateToken);
        Assert.Equal(new EntityTag(false, "etag"), conditions[1].EntityTag);
    }

    [Theory]
    [InlineData("<urn:uuid:181d4fae-7d8c-11d0-a765-00a0c91e6bf2>", true)]
    [InlineData(" <opaquelocktoken:abc> ", true)]
    [InlineData("urn:uuid:181d4fae", false)]
    [InlineData("<relative>", false)]
    [InlineData("<urn:a> <urn:b>", false)]
    public void Lock_tokens_are_coded_urls(string value, bool valid)
    {
        Assert.Equal(valid, HeaderParser.TryParseCodedUrl(value, out _));
    }
}
