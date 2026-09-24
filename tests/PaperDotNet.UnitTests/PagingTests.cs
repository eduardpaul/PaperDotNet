using PaperDotNet.Abstractions;
using PaperDotNet.Api;

namespace PaperDotNet.UnitTests;

public sealed class PagingTests
{
    [Fact]
    public void Cursor_round_trips()
    {
        var id = Ids.New();

        var cursor = PageRequest.EncodeCursor(id);

        Assert.True(PageRequest.TryDecodeCursor(cursor, out var decoded));
        Assert.Equal(id, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AAAA")]
    public void Invalid_cursor_is_rejected(string value) =>
        Assert.False(PageRequest.TryDecodeCursor(value, out _));

    [Fact]
    public async Task Ids_are_uuid_v7_and_time_ordered_in_their_canonical_form()
    {
        var first = Ids.New();
        await Task.Delay(2, TestContext.Current.CancellationToken);
        var second = Ids.New();

        Assert.Equal(7, first.Version);

        // PostgreSQL orders uuids by their canonical (RFC 9562) bytes, which for v7 is creation time.
        // Note: System.Guid.CompareTo uses a different byte order, so paging always compares in SQL.
        Assert.True(string.CompareOrdinal(first.ToString(), second.ToString()) < 0);
    }
}
