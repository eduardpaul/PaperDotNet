using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PaperDotNet.Calendar.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Real list/calendar/workflow pipelines; only the external feed transport is replaced.</summary>
public sealed class CalendarSourceTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Root(Guid ws, Guid list) => $"/v1.0/workspaces/{ws}/lists/{list}";
    private static string Feed(string title) => $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:shared-uid\r\nDTSTART:20261005T100000Z\r\nDTEND:20261005T110000Z\r\nSUMMARY:{title}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
    private const string Empty = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n";

    private static async Task<Guid> ListAsync(HttpClient client, Guid ws, string name)
    {
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey = "calendar" }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> AddAsync(HttpClient client, string root, string name, string url)
    {
        var response = await client.PostAsJsonAsync(root + "/calendarSources", new { name, url }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var source = await response.ReadJsonAsync();
        Assert.False(source.TryGetProperty("url", out _));
        await WaitAsync(client, root, source.GetProperty("id").GetGuid());
        return source;
    }

    private static async Task<JsonElement> WaitAsync(HttpClient client, string root, Guid id, DateTimeOffset? after = null, bool error = false)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var sources = await (await client.GetAsync(root + "/calendarSources", Ct)).ReadJsonAsync();
            var source = sources.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);
            if (!source.GetProperty("refreshing").GetBoolean()
                && (error ? source.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.String
                    : source.TryGetProperty("lastSuccess", out var success) && success.ValueKind == JsonValueKind.String
                    && (after == null || source.GetProperty("lastSuccess").GetDateTimeOffset() > after))) { return source; }
            await Task.Delay(50, Ct);
        }
        throw new InvalidOperationException("Source did not finish refreshing.");
    }

    private static async Task<JsonElement> RefreshAsync(HttpClient client, string root, Guid id, bool error = false)
    {
        var sources = await (await client.GetAsync(root + "/calendarSources", Ct)).ReadJsonAsync();
        var before = sources.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);
        var last = before.GetProperty("lastSuccess").GetDateTimeOffset();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"{root}/calendarSources/{id}/refresh", null, Ct)).StatusCode);
        return await WaitAsync(client, root, id, last, error);
    }

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient client, string root) =>
        (await (await client.GetAsync(root + "/items", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();

    [Fact]
    public async Task Multiple_sources_and_lists_have_independent_identities_and_restore_returning_events()
    {
        await factory.CreateTenantAsync("cal-sources");
        var client = await ApiClient.CreateAsync(factory, "cal-sources");
        var ws = await client.CreateWorkspaceAsync("Team");
        var first = await ListAsync(client, ws, "Combined");
        var second = await ListAsync(client, ws, "Another");
        var root = Root(ws, first);
        var other = Root(ws, second);
        const string urlA = "https://calendar.example/a-secret.ics";
        const string urlB = "https://calendar.example/b-secret.ics";
        CalendarFeedTransport.Feeds[urlA] = (HttpStatusCode.OK, Feed("A"));
        CalendarFeedTransport.Feeds[urlB] = (HttpStatusCode.OK, Feed("B"));
        var a = await AddAsync(client, root, "Source A", urlA);
        var b = await AddAsync(client, root, "Source B", urlB);
        var aId = a.GetProperty("id").GetGuid();
        await AddAsync(client, other, "Same feed", urlA);
        var template = await client.GetStringAsync($"/v1.0/provisioning/export?workspaceId={ws}", Ct);
        Assert.Contains("CalendarSources", template, StringComparison.Ordinal);
        Assert.DoesNotContain("a-secret", template, StringComparison.Ordinal);
        Assert.DoesNotContain("b-secret", template, StringComparison.Ordinal);
        var exported = await client.GetStringAsync(root + "/calendar.ics", Ct);
        var exportedUids = exported.ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith("UID:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, exportedUids.Count);
        Assert.Equal(2, exportedUids.Distinct(StringComparer.Ordinal).Count());
        var initial = await ItemsAsync(client, root);
        Assert.Equal(2, initial.Count);
        var itemA = initial.Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "A");
        var itemB = initial.Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "B");
        await RefreshAsync(client, root, aId);
        Assert.Equal(2, (await ItemsAsync(client, root)).Count);

        var managed = await (await client.GetAsync($"{root}/items/{itemA.GetProperty("id").GetGuid()}/calendarSource", Ct)).ReadJsonAsync();
        Assert.Equal(aId, managed.GetProperty("sourceId").GetGuid());
        var edit = new HttpRequestMessage(HttpMethod.Patch, $"{root}/items/{itemA.GetProperty("id").GetGuid()}")
        { Content = JsonContent.Create(new { fields = new { title = "Local edit" } }) };
        var latestA = await (await client.GetAsync($"{root}/items/{itemA.GetProperty("id").GetGuid()}", Ct)).ReadJsonAsync();
        edit.Headers.TryAddWithoutValidation("If-Match", latestA.GetProperty("@odata.etag").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(edit, Ct)).StatusCode);

        CalendarFeedTransport.Feeds[urlA] = (HttpStatusCode.OK, Empty);
        await RefreshAsync(client, root, aId);
        Assert.Equal(itemB.GetProperty("id").GetGuid(), Assert.Single(await ItemsAsync(client, root)).GetProperty("id").GetGuid());
        Assert.Single(await ItemsAsync(client, other)); // no refresh was requested for the other list
        CalendarFeedTransport.Feeds[urlA] = (HttpStatusCode.OK, Feed("A changed"));
        await RefreshAsync(client, root, aId);
        Assert.Equal(itemA.GetProperty("id").GetGuid(), (await ItemsAsync(client, root))
            .Single(i => i.GetProperty("fields").GetProperty("title").GetString() == "A changed").GetProperty("id").GetGuid());

        CalendarFeedTransport.Feeds[urlA] = (HttpStatusCode.OK, "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\n");
        var failed = await RefreshAsync(client, root, aId, true);
        Assert.DoesNotContain("a-secret", failed.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(2, (await ItemsAsync(client, root)).Count);
        CalendarFeedTransport.Feeds[urlA] = (HttpStatusCode.Forbidden, "");
        await RefreshAsync(client, root, aId, true);
        Assert.Equal(2, (await ItemsAsync(client, root)).Count);

        var sources = await (await client.GetAsync(root + "/calendarSources", Ct)).ReadJsonAsync();
        var currentB = sources.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == b.GetProperty("id").GetGuid());
        var remove = new HttpRequestMessage(HttpMethod.Delete, $"{root}/calendarSources/{currentB.GetProperty("id").GetGuid()}");
        remove.Headers.TryAddWithoutValidation("If-Match", currentB.GetProperty("@odata.etag").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(remove, Ct)).StatusCode);
        Assert.Equal(2, (await ItemsAsync(client, root)).Count); // removing a source retains its local copies
    }

    [Fact]
    public async Task Recurrence_and_exception_changes_replace_the_source_snapshot()
    {
        await factory.CreateTenantAsync("cal-source-series");
        var client = await ApiClient.CreateAsync(factory, "cal-source-series");
        var ws = await client.CreateWorkspaceAsync("Team");
        var list = await ListAsync(client, ws, "Events");
        var root = Root(ws, list);
        const string url = "https://calendar.example/series.ics";
        const string series = "BEGIN:VEVENT\r\nUID:series\r\nDTSTART;TZID=Europe/Berlin:20261005T090000\r\nDTEND;TZID=Europe/Berlin:20261005T091500\r\nRRULE:FREQ=WEEKLY;COUNT=5\r\nSUMMARY:Standup\r\n{0}END:VEVENT\r\n";
        static string Calendar(string body) => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + body + "END:VCALENDAR\r\n";
        CalendarFeedTransport.Feeds[url] = (HttpStatusCode.OK, Calendar(series.Replace("{0}", "EXDATE;TZID=Europe/Berlin:20261012T090000\r\n", StringComparison.Ordinal)));
        var source = await AddAsync(client, root, "Series", url);
        var sourceId = source.GetProperty("id").GetGuid();
        var item = Assert.Single(await ItemsAsync(client, root));
        var itemId = item.GetProperty("id").GetGuid();
        var recurrence = await (await client.GetAsync($"{root}/items/{itemId}/series", Ct)).ReadJsonAsync();
        Assert.Single(recurrence.GetProperty("cancelled").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{root}/items/{itemId}/series", new { rule = "FREQ=DAILY", timeZone = "UTC" }, Ct)).StatusCode);
        CalendarFeedTransport.Feeds[url] = (HttpStatusCode.OK, Calendar(series.Replace("{0}", "", StringComparison.Ordinal)));
        await RefreshAsync(client, root, sourceId);
        recurrence = await (await client.GetAsync($"{root}/items/{itemId}/series", Ct)).ReadJsonAsync();
        Assert.Empty(recurrence.GetProperty("cancelled").EnumerateArray());
        var range = await (await client.GetAsync(root + "/calendar?start=2026-10-01T00:00:00Z&end=2026-11-15T00:00:00Z", Ct)).ReadJsonAsync();
        var starts = range.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("start").GetDateTimeOffset()).ToList();
        Assert.Contains(DateTimeOffset.Parse("2026-10-26T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture), starts);
        Assert.Equal(5, starts.Count);
    }

    [Fact]
    public async Task Source_endpoints_are_tenant_isolated_and_require_list_management()
    {
        await factory.CreateTenantAsync("cal-sources-iso-a");
        await factory.CreateTenantAsync("cal-sources-iso-b");
        var a = await ApiClient.CreateAsync(factory, "cal-sources-iso-a");
        var b = await ApiClient.CreateAsync(factory, "cal-sources-iso-b");
        var ws = await a.CreateWorkspaceAsync("Private");
        var list = await ListAsync(a, ws, "Events");
        var root = Root(ws, list);
        var url = "https://calendar.example/isolation.ics";
        CalendarFeedTransport.Feeds[url] = (HttpStatusCode.OK, Feed("Private"));
        var source = await AddAsync(a, root, "Private source", url);
        var id = source.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(root + "/calendarSources", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync(root + "/calendarSources", new { name = "Leak", url }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"{root}/calendarSources/{id}/refresh", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"{root}/calendarSources/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await a.DeleteAsync($"{root}/calendarSources/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync(root + "/calendarSources", new { name = "Invalid", url = "file:///etc/passwd" }, Ct)).StatusCode);
        var user = await a.PostAsJsonAsync("/v1.0/users", new { userName = "reader", password = "reader-password-1" }, Ct);
        var userId = (await user.ReadJsonAsync()).GetProperty("id").GetGuid();
        await a.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId, role = "visitor" }, Ct);
        var reader = await ApiClient.CreateAsync(factory, "cal-sources-iso-a", "reader", "reader-password-1");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(root + "/calendarSources", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(root + "/calendarSources", new { name = "Forbidden", url }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"{root}/calendarSources/{id}/refresh", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{root}/calendarSources/{id}", Ct)).StatusCode);
    }
}

internal sealed class CalendarFeedTransport : HttpMessageHandler
{
    public static readonly ConcurrentDictionary<string, (HttpStatusCode Status, string Text)> Feeds = new(StringComparer.Ordinal);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (status, text) = Feeds.TryGetValue(request.RequestUri!.AbsoluteUri, out var feed) ? feed : (HttpStatusCode.NotFound, "");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "text/calendar") });
    }
}
