#nullable enable // PaperDotNet: upstream tests of the vendored WebDAV library (ADR-0047).
using System;

using FubarDev.WebDavServer.Parsing;

using Xunit;

namespace FubarDev.WebDavServer.Models.Tests;

public class IfHeaderTests
{
    [Fact]
    public void TestIfHeaderWithSingleNoTagList()
    {
        // PaperDotNet: HeaderParser replaces the Yoakke-generated parser.
        Assert.True(HeaderParser.TryParseIfHeader("( <http://statetoken> not <http://statetoken> [\"etag\"] not [w/\"etag\"] )", out var header));
        Assert.True(header.IsNoTagList);
        Assert.Collection(
            header.NoTagLists,
            item => Assert.Collection(
                item.List,
                condition => Assert.Equal(new IfCondition(false, new Uri("http://statetoken"), null), condition),
                condition => Assert.Equal(new IfCondition(true, new Uri("http://statetoken"), null), condition),
                condition => Assert.Equal(new IfCondition(false, null, new EntityTag(false, "etag")), condition),
                condition => Assert.Equal(new IfCondition(true, null, new EntityTag(true, "etag")), condition)));
    }

    [Fact]
    public void TestIfHeaderWithMultipleNoTagLists()
    {
        // PaperDotNet: HeaderParser replaces the Yoakke-generated parser.
        Assert.True(HeaderParser.TryParseIfHeader("( <http://statetoken> ) ( not <http://statetoken> ) ( [\"etag\"] ) ( not [w/\"etag\"] )", out var header));
        Assert.True(header.IsNoTagList);
        Assert.Collection(
            header.NoTagLists,
            item => Assert.Collection(
                item.List,
                condition => Assert.Equal(new IfCondition(false, new Uri("http://statetoken"), null), condition)),
            item => Assert.Collection(
                item.List,
                condition => Assert.Equal(new IfCondition(true, new Uri("http://statetoken"), null), condition)),
            item => Assert.Collection(
                item.List,
                condition => Assert.Equal(new IfCondition(false, null, new EntityTag(false, "etag")), condition)),
            item => Assert.Collection(
                item.List,
                condition => Assert.Equal(new IfCondition(true, null, new EntityTag(true, "etag")), condition)));
    }

    [Fact]
    public void TestIfHeaderWithSingleTaggedList()
    {
        // PaperDotNet: HeaderParser replaces the Yoakke-generated parser.
        Assert.True(HeaderParser.TryParseIfHeader("</test> ( <http://st> not <http://st> [\"t\"] not [w/\"t\"] )", out var header));
        Assert.True(header.IsTaggedList);
        Assert.Collection(
            header.TaggedLists,
            item =>
            {
                Assert.Equal("/test", item.ResourceTag.ToString());
                Assert.Collection(
                    item.Lists,
                    list => Assert.Collection(
                        list,
                        condition => Assert.Equal(new IfCondition(false, new Uri("http://st"), null), condition),
                        condition => Assert.Equal(new IfCondition(true, new Uri("http://st"), null), condition),
                        condition => Assert.Equal(new IfCondition(false, null, new EntityTag(false, "t")), condition),
                        condition => Assert.Equal(new IfCondition(true, null, new EntityTag(true, "t")), condition)));
            });
    }

    [Fact]
    public void TestIfHeaderWithSingleTaggedListWithMultipleLists()
    {
        // PaperDotNet: HeaderParser replaces the Yoakke-generated parser.
        Assert.True(HeaderParser.TryParseIfHeader("</test> ( <http://st> ) ( not <http://st> ) ( [\"t\"] ) ( not [w/\"t\"] )", out var header));
        Assert.True(header.IsTaggedList);
        Assert.Collection(
            header.TaggedLists,
            item =>
            {
                Assert.Equal("/test", item.ResourceTag.ToString());
                Assert.Collection(
                    item.Lists,
                    list => Assert.Collection(
                        list,
                        condition => Assert.Equal(new IfCondition(false, new Uri("http://st"), null), condition)),
                    list => Assert.Collection(
                        list,
                        condition => Assert.Equal(new IfCondition(true, new Uri("http://st"), null), condition)),
                    list => Assert.Collection(
                        list,
                        condition => Assert.Equal(new IfCondition(false, null, new EntityTag(false, "t")), condition)),
                    list => Assert.Collection(
                        list,
                        condition => Assert.Equal(new IfCondition(true, null, new EntityTag(true, "t")), condition)));
            });
    }

    [Fact]
    public void TestIfHeaderWithMultipleTaggedLists()
    {
        // PaperDotNet: HeaderParser replaces the Yoakke-generated parser.
        Assert.True(HeaderParser.TryParseIfHeader("</test1> ( <http://st> ) </test2> ( not <http://st> )", out var header));
        Assert.True(header.IsTaggedList);
        Assert.Collection(
            header.TaggedLists,
            item =>
            {
                Assert.Equal("/test1", item.ResourceTag.ToString());
                Assert.Collection(
                    item.Lists,
                    list => Assert.Collection(
                        list,
                        condition => Assert.Equal(new IfCondition(false, new Uri("http://st"), null), condition)));
            },
            item =>
            {
                Assert.Equal("/test2", item.ResourceTag.ToString());
                Assert.Collection(
                    item.Lists,
                    list => Assert.Collection(
                        list,
                        condition => Assert.Equal(new IfCondition(true, new Uri("http://st"), null), condition)));
            });
    }
}
