using System.Globalization;
using System.Text.Json.Nodes;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Calendar.Features;

/// <summary>An event occurrence or a due task in a time range (CAL-03).</summary>
public sealed record CalendarEntry(
    string Kind,
    Guid WorkspaceId,
    Guid ListId,
    string ListName,
    Guid ItemId,
    string? Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string? Location,
    bool Recurring,
    DateTimeOffset? OccurrenceStart,
    Guid? MasterItemId,
    string? Status)
{
    public IReadOnlyList<Guid> Attendees { get; init; } = [];

    /// <summary>Minutes before the start to remind attendees (CAL-01, NTF-02).</summary>
    public int? ReminderMinutes { get; init; }

    public Guid? CreatedBy { get; init; }
}

/// <summary>The time-related values of an event item.</summary>
internal sealed record EventTimes(DateTimeOffset Start, DateTimeOffset End, bool AllDay)
{
    public TimeSpan Duration => End - Start;

    public static EventTimes? From(JsonObject fields)
    {
        if (!TryParse(fields["start"], out var start))
        {
            return null;
        }

        var allDay = fields["allDay"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        var end = TryParse(fields["end"], out var parsed) && parsed >= start ? parsed : start;
        return new EventTimes(start, end, allDay);
    }

    public static bool TryParse(JsonNode? node, out DateTimeOffset value)
    {
        value = default;
        return node is JsonValue text && text.TryGetValue<string>(out var s)
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }
}

/// <summary>
/// Reads events and due tasks for time ranges, expanding recurring series with their exceptions in
/// the series' time zone (Ical.Net). Items come through <see cref="IListItemStore"/>, so the caller's
/// permissions apply (ADR-0011).
/// </summary>
internal sealed class CalendarService(IListItemStore items, CalendarDbContext db)
{
    public const string EventKey = "event";
    public const string TaskKey = "task";
    public const int MaxEntries = 2000;
    private const int MaxExpansion = 100_000;

    /// <summary>Calendar and task lists the caller can read (optionally one list).</summary>
    public async Task<List<ListData>> ListsAsync(Guid? workspaceId, Guid? listId, CancellationToken ct)
    {
        if (listId is { } id && workspaceId is { } ws)
        {
            return await items.GetListAsync(ws, id, ct) is { } list && IsCalendarOrTaskList(list) ? [list] : [];
        }

        return [.. (await items.GetListsAsync(workspaceId, null, ct)).Where(IsCalendarOrTaskList)];
    }

    public static bool IsCalendarOrTaskList(ListData list) => list.ContentTypeKeys.Contains(EventKey) || list.ContentTypeKeys.Contains(TaskKey);

    /// <summary>Events overlapping [<paramref name="from"/>, <paramref name="to"/>) and, when asked, tasks due in it; sorted by start.</summary>
    public async Task<List<CalendarEntry>> RangeAsync(IReadOnlyList<ListData> lists, DateTimeOffset from, DateTimeOffset to, bool includeTasks, CancellationToken ct)
    {
        var entries = new List<CalendarEntry>();
        foreach (var list in lists)
        {
            if (list.ContentTypeKeys.Contains(EventKey))
            {
                await AddEventsAsync(list, from, to, entries, ct);
            }

            if (includeTasks && list.ContentTypeKeys.Contains(TaskKey))
            {
                await AddTasksAsync(list, from, to, entries, ct);
            }
        }

        return [.. entries.OrderBy(e => e.Start).ThenBy(e => e.Title, StringComparer.CurrentCulture).Take(MaxEntries)];
    }

    private async Task AddEventsAsync(ListData list, DateTimeOffset from, DateTimeOffset to, List<CalendarEntry> entries, CancellationToken ct)
    {
        var recurrences = await db.Recurrences.AsNoTracking().Where(r => r.ListId == list.Id).ToListAsync(ct);
        var masterIds = recurrences.Select(r => r.ItemId).ToList();
        var exceptions = await db.OccurrenceChanges.AsNoTracking().Where(e => masterIds.Contains(e.MasterItemId)).ToListAsync(ct);
        var overrides = exceptions.Where(e => e.OverrideItemId is not null).ToDictionary(e => e.OverrideItemId!.Value, e => e.MasterItemId);

        var filter = $"fields/start lt {Literal(to)} and (fields/end gt {Literal(from)} or fields/start ge {Literal(from)})";
        var (found, _) = await items.QueryAsync(list.WorkspaceId, list.Id, new ListItemQuery(filter, "fields/start", 1000), ct);
        foreach (var item in found.Where(i => !masterIds.Contains(i.Id)))
        {
            if (EventTimes.From(item.Fields) is { } times)
            {
                entries.Add(Entry(list, item, times.Start, times, recurring: false, occurrence: null, overrides.TryGetValue(item.Id, out var master) ? master : null));
            }
        }

        foreach (var recurrence in recurrences)
        {
            var item = await items.GetAsync(list.WorkspaceId, list.Id, recurrence.ItemId, ct);
            if (item is null || EventTimes.From(item.Fields) is not { } times)
            {
                continue;
            }

            var skipped = exceptions.Where(e => e.MasterItemId == item.Id).Select(e => e.OriginalStart.ToUniversalTime()).ToHashSet();
            foreach (var start in Occurrences(recurrence, times, to).Where(s => s + times.Duration > from || (times.Duration == TimeSpan.Zero && s >= from)))
            {
                if (!skipped.Contains(start))
                {
                    entries.Add(Entry(list, item, start, times, recurring: true, occurrence: start, master: null));
                }
            }
        }
    }

    private async Task AddTasksAsync(ListData list, DateTimeOffset from, DateTimeOffset to, List<CalendarEntry> entries, CancellationToken ct)
    {
        var first = DateOnly.FromDateTime(from.UtcDateTime);
        var last = DateOnly.FromDateTime(to.UtcDateTime.AddTicks(-1));
        var filter = $"fields/dueDate ge {first:yyyy-MM-dd} and fields/dueDate le {last:yyyy-MM-dd}";
        var (found, _) = await items.QueryAsync(list.WorkspaceId, list.Id, new ListItemQuery(filter, "fields/dueDate", 1000), ct);
        foreach (var task in found)
        {
            if (task.Fields["dueDate"] is JsonValue due && DateOnly.TryParse(due.GetValue<string>(), CultureInfo.InvariantCulture, out var day))
            {
                var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                entries.Add(new CalendarEntry("task", task.WorkspaceId, task.ListId, list.Name, task.Id, Title(task), start, start.AddDays(1), true, null, false, null, null,
                    task.Fields["status"] is JsonValue status ? status.GetValue<string>() : null));
            }
        }
    }

    /// <summary>Starts (UTC) of a series' occurrences before <paramref name="until"/>, in its time zone.</summary>
    public static IEnumerable<DateTimeOffset> Occurrences(EventRecurrence recurrence, EventTimes times, DateTimeOffset until)
    {
        var series = new CalendarEvent { DtStart = SeriesStart(times, recurrence.TimeZone), RecurrenceRule = new RecurrencePattern(recurrence.Rule) };
        return series.GetOccurrences(null, null)
            .Take(MaxExpansion)
            .Select(o => times.AllDay
                ? new DateTimeOffset(o.Period.StartTime.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : new DateTimeOffset(o.Period.StartTime.AsUtc, TimeSpan.Zero))
            .TakeWhile(s => s < until);
    }

    /// <summary>The series start as iCalendar value: a date for all-day events, else local time in the zone.</summary>
    public static CalDateTime SeriesStart(EventTimes times, string timeZone) => times.AllDay
        ? new CalDateTime(DateOnly.FromDateTime(times.Start.UtcDateTime))
        : Local(times.Start, timeZone);

    public static CalDateTime Local(DateTimeOffset instant, string timeZone)
    {
        if (timeZone == "UTC")
        {
            return new CalDateTime(instant.UtcDateTime, "UTC");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        return new CalDateTime(DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, zone).DateTime, DateTimeKind.Unspecified), timeZone);
    }

    public static bool IsKnownTimeZone(string timeZone) => timeZone == "UTC" || TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _);

    public static string Literal(DateTimeOffset value) => FieldFormats.DateTime(value);

    public static string? Title(ListItemData item) => item.Fields["title"]?.GetValue<string>();

    private static CalendarEntry Entry(ListData list, ListItemData item, DateTimeOffset start, EventTimes times, bool recurring, DateTimeOffset? occurrence, Guid? master) =>
        new("event", item.WorkspaceId, item.ListId, list.Name, item.Id, Title(item), start, start + times.Duration, times.AllDay,
            item.Fields["location"] is JsonValue location ? location.GetValue<string>() : null, recurring || master is not null, occurrence, master, null)
        {
            Attendees = item.Fields["attendees"] is JsonArray attendees ? [.. attendees.Select(a => Guid.Parse(a!.GetValue<string>()))] : [],
            ReminderMinutes = item.Fields["reminderMinutes"] is JsonValue reminder && reminder.TryGetValue<decimal>(out var minutes) ? (int)minutes : null,
            CreatedBy = item.CreatedBy,
        };
}
