using System.Net;
using System.Net.Http.Headers;
using PaperDotNet.Calendar.Features;

namespace PaperDotNet.UnitTests;

public sealed class CalendarSourceHttpTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Only_public_addresses_are_connected(string address, bool expected) =>
        Assert.Equal(expected, CalendarSourceHttp.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task Conditional_fetch_keeps_the_snapshot_and_redirects_cannot_downgrade_https()
    {
        using var fetcher = new CalendarSourceHttp(new HttpClient(new Stub(request =>
        {
            Assert.True(request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("PaperDotNet.PrivateHttpRequest"), out var privateRequest) && privateRequest);
            Assert.Equal("\"feed-v1\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        })));
        var download = await fetcher.DownloadAsync("https://calendar.example/feed.ics", "\"feed-v1\"", null, TestContext.Current.CancellationToken);
        Assert.Null(download.Text);
        Assert.Equal("\"feed-v1\"", download.ETag);
        using var redirected = new CalendarSourceHttp(new HttpClient(new Stub(_ =>
            new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1/secret") } })));
        var error = await Assert.ThrowsAsync<CalendarSourceException>(() => redirected.DownloadAsync("https://calendar.example/secret-token.ics", null, null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("secret-token", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_streams_are_rejected_even_without_a_content_length()
    {
        using var client = new CalendarSourceHttp(new HttpClient(new Stub(_ =>
        {
            var content = new StreamContent(new UnknownLengthStream(new byte[CalendarSourceHttp.MaxBytes + 1]));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        })));
        await Assert.ThrowsAsync<CalendarSourceException>(() => client.DownloadAsync("https://calendar.example/feed.ics", null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Snapshots_must_be_complete_and_have_stable_identities()
    {
        Assert.Empty(CalendarSourceReplication.ParseSnapshot("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n"));
        Assert.Throws<CalendarSourceException>(() => CalendarSourceReplication.ParseSnapshot("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\n"));
        var one = "BEGIN:VEVENT\r\nUID:same\r\nDTSTART:20261002T120000Z\r\nSUMMARY:One\r\nEND:VEVENT\r\n";
        Assert.Throws<CalendarSourceException>(() => CalendarSourceReplication.ParseSnapshot("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + one + one + "END:VCALENDAR\r\n"));
    }

    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
