using System.Net;
using System.Text;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Calendar: event times, recurrence with exceptions, time ranges, iCalendar and feeds (CAL-01…04).</summary>
public sealed class CalendarTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<string> ListAsync(HttpClient client, string ws, string templateKey, string name)
    {
        using var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static string Item(string ws, string list, string item) => $"{Api.Items(ws, list)}/{item}";

    private static async Task<List<JsonElement>> RangeAsync(HttpClient client, string url) =>
        [.. (await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()];

    /// <summary>A weekly standup at 09:00 Berlin time, 15 minutes, six times from 5 October 2026 (DST ends on 25 October).</summary>
    private static async Task<string> StandupAsync(HttpClient client, string ws, string list)
    {
        var standup = (await Api.CreateItemAsync(client, ws, list, new { title = "Standup", start = "2026-10-05T07:00:00Z", end = "2026-10-05T07:15:00Z", location = "Room 1" })).Id();
        var set = await client.PutAsJsonAsync($"{Item(ws, list, standup)}/series", new { rule = "FREQ=WEEKLY;COUNT=6", timeZone = "Europe/Berlin" }, Ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        return standup;
    }

    [Fact]
    public async Task Event_times_are_kept_consistent()
    {
        var client = _client;
        var ws = await Api.CreateWorkspaceAsync(client, "Team");
        var list = await ListAsync(client, ws, "calendar", "Events");

        var timed = await Api.CreateItemAsync(client, ws, list, new { title = "Call", start = "2026-10-05T10:00:00Z" });
        Assert.StartsWith("2026-10-05T11:00:00", timed.GetProperty("fields").GetProperty("end").GetString(), StringComparison.Ordinal);
        var allDay = await Api.CreateItemAsync(client, ws, list, new { title = "Holiday", start = "2026-10-03T15:30:00Z", allDay = true });
        Assert.StartsWith("2026-10-03T00:00:00", allDay.GetProperty("fields").GetProperty("start").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("2026-10-04T00:00:00", allDay.GetProperty("fields").GetProperty("end").GetString(), StringComparison.Ordinal);

        var backwards = await client.PostAsJsonAsync(Api.Items(ws, list), new { fields = new { title = "Wrong", start = "2026-10-05T10:00:00Z", end = "2026-10-05T09:00:00Z" } }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, backwards.StatusCode);
    }

    [Fact]
    public async Task Recurring_events_expand_in_their_time_zone_with_exceptions()
    {
        var client = _client;
        var ws = await Api.CreateWorkspaceAsync(client, "Team");
        var list = await ListAsync(client, ws, "calendar", "Events");
        var tasks = await ListAsync(client, ws, "tasks", "Tasks");
        await Api.CreateItemAsync(client, ws, tasks, new { title = "Send report", dueDate = "2026-10-20" });
        var standup = await StandupAsync(client, ws, list);
        var range = $"/v1.0/workspaces/{ws}/lists/{list}/calendar?start=2026-10-01T00:00:00Z&end=2026-11-15T00:00:00Z";

        var all = await RangeAsync(client, range);
        Assert.Equal(
            ["2026-10-05T07:00:00+00:00", "2026-10-12T07:00:00+00:00", "2026-10-19T07:00:00+00:00", "2026-10-26T08:00:00+00:00", "2026-11-02T08:00:00+00:00", "2026-11-09T08:00:00+00:00"],
            all.Select(e => e.GetProperty("start").GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(all, e => Assert.Equal(15, (e.GetProperty("end").GetDateTimeOffset() - e.GetProperty("start").GetDateTimeOffset()).TotalMinutes));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Item(ws, list, standup)}/series/occurrences/2026-10-12T07:00:00Z", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Item(ws, list, standup)}/series/occurrences/2026-10-13T07:00:00Z", Ct)).StatusCode);
        var moved = await client.PutAsJsonAsync($"{Item(ws, list, standup)}/series/occurrences/2026-10-19T07:00:00Z",
            new { fields = new { title = "Standup (moved)", start = "2026-10-19T08:00:00Z" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var movedId = (await moved.JsonAsync(HttpStatusCode.OK)).GetProperty("itemId").GetString();

        var after = await RangeAsync(client, range);
        Assert.Equal(5, after.Count);
        Assert.DoesNotContain(after, e => e.GetProperty("start").GetDateTimeOffset() == DateTimeOffset.Parse("2026-10-12T07:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var changed = Assert.Single(after, e => e.GetProperty("itemId").GetString() == movedId);
        Assert.Equal("Standup (moved)", changed.GetProperty("title").GetString());
        Assert.Equal(standup, changed.GetProperty("masterItemId").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture), changed.GetProperty("start").GetDateTimeOffset());
        Assert.Equal("Room 1", changed.GetProperty("location").GetString());

        // Across lists, with the task due in the range.
        var mine = await RangeAsync(client, "/v1.0/me/calendar?start=2026-10-01T00:00:00Z&end=2026-11-15T00:00:00Z");
        var task = Assert.Single(mine, e => e.GetProperty("kind").GetString() == "task");
        Assert.Equal("Send report", task.GetProperty("title").GetString());
        Assert.True(task.GetProperty("allDay").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1.0/me/calendar?start=2026-01-01T00:00:00Z&end=2028-01-01T00:00:00Z", Ct)).StatusCode);

        var recurrence = await (await client.GetAsync($"{Item(ws, list, standup)}/series", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(1, recurrence.GetProperty("cancelled").GetArrayLength());
        Assert.Equal(1, recurrence.GetProperty("moved").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Item(ws, list, standup)}/series", new { rule = "FREQ=DAILY", timeZone = "Mars/Olympus" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task ICalendar_export_and_import_round_trip()
    {
        var client = _client;
        var ws = await Api.CreateWorkspaceAsync(client, "Team");
        var source = await ListAsync(client, ws, "calendar", "Source");
        var target = await ListAsync(client, ws, "calendar", "Copy");
        var standup = await StandupAsync(client, ws, source);
        await client.DeleteAsync($"{Item(ws, source, standup)}/series/occurrences/2026-10-12T07:00:00Z", Ct);
        await client.PutAsJsonAsync($"{Item(ws, source, standup)}/series/occurrences/2026-10-19T07:00:00Z", new { fields = new { start = "2026-10-19T08:00:00Z" } }, Ct);
        await Api.CreateItemAsync(client, ws, source, new { title = "Offsite", start = "2026-10-21T00:00:00Z", allDay = true });

        var ics = await client.GetStringAsync($"/v1.0/workspaces/{ws}/lists/{source}/calendar.ics", Ct);
        Assert.Contains("RRULE:FREQ=WEEKLY;COUNT=6", ics, StringComparison.Ordinal);
        Assert.Contains("EXDATE;TZID=Europe/Berlin:20261012T090000", ics, StringComparison.Ordinal);
        Assert.Contains("RECURRENCE-ID;TZID=Europe/Berlin:20261019T090000", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART;VALUE=DATE:20261021", ics, StringComparison.Ordinal);
        Assert.Contains("BEGIN:VTIMEZONE", ics, StringComparison.Ordinal);

        async Task<JsonElement> ImportAsync(string text) =>
            await (await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{target}/calendar/import", new StringContent(text, Encoding.UTF8, "text/calendar"), Ct)).JsonAsync(HttpStatusCode.OK);

        var first = await ImportAsync(ics);
        Assert.Equal(3, first.GetProperty("created").GetInt32());
        var again = await ImportAsync(ics);
        Assert.Equal(0, again.GetProperty("created").GetInt32());
        Assert.Equal(3, again.GetProperty("updated").GetInt32());

        string Starts(IEnumerable<JsonElement> entries) => string.Join(",", entries.Select(e => e.GetProperty("start").GetDateTimeOffset().UtcDateTime.ToString("MMdd-HH", System.Globalization.CultureInfo.InvariantCulture)));
        var original = await RangeAsync(client, $"/v1.0/workspaces/{ws}/lists/{source}/calendar?start=2026-10-01T00:00:00Z&end=2026-11-15T00:00:00Z");
        var copied = await RangeAsync(client, $"/v1.0/workspaces/{ws}/lists/{target}/calendar?start=2026-10-01T00:00:00Z&end=2026-11-15T00:00:00Z");
        Assert.Equal(Starts(original), Starts(copied));

        var broken = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{target}/calendar/import", new StringContent("not a calendar", Encoding.UTF8, "text/calendar"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
    }

    [Fact]
    public async Task Feeds_serve_calendars_without_login_until_revoked()
    {
        var client = _client;
        var ws = await Api.CreateWorkspaceAsync(client, "Team");
        var list = await ListAsync(client, ws, "calendar", "Events");
        await Api.CreateItemAsync(client, ws, list, new { title = "Board meeting", start = "2026-10-07T12:00:00Z" });

        var created = await client.PostAsJsonAsync("/v1.0/me/calendarFeeds", new { name = "Team", workspaceId = ws, listId = list }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var feed = await created.JsonAsync(HttpStatusCode.Created);
        var url = new Uri(feed.GetProperty("url").GetString()!);

        using var anonymous = _host.CreateClient();
        var response = await anonymous.GetAsync(url.PathAndQuery, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("SUMMARY:Board meeting", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var listed = await (await client.GetAsync("/v1.0/me/calendarFeeds", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.False(Assert.Single(listed.EnumerateArray()).TryGetProperty("url", out _)); // the secret is shown once

        var tampered = url.PathAndQuery.Replace(".ics", "x.ics", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(tampered, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/v1.0/me/calendarFeeds/{feed.Id()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url.PathAndQuery, Ct)).StatusCode);
    }

    [Fact]
    public async Task Calendars_are_isolated_by_tenant()
    {
        var a = _client;
        var b = await _host.CreateTenantAsync("other");
        var ws = await Api.CreateWorkspaceAsync(a, "Private");
        var list = await ListAsync(a, ws, "calendar", "Events");
        var standup = await StandupAsync(a, ws, list);

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/calendar?start=2026-10-01T00:00:00Z&end=2026-11-01T00:00:00Z", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/calendar.ics", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"{Item(ws, list, standup)}/series", Ct)).StatusCode);
        Assert.Empty(await RangeAsync(b, "/v1.0/me/calendar?start=2026-10-01T00:00:00Z&end=2026-11-01T00:00:00Z"));
        var feed = await b.PostAsJsonAsync("/v1.0/me/calendarFeeds", new { workspaceId = ws, listId = list }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, feed.StatusCode);
    }
}
