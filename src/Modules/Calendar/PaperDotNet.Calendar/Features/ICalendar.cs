using System.Globalization;
using System.Text.Json.Nodes;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Lists.Contracts;
using IcalCalendar = Ical.Net.Calendar;

namespace PaperDotNet.Calendar.Features;

public sealed record ImportResult(int Created, int Updated, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// iCalendar export and import (CAL-04) with Ical.Net: events with RRULE, EXDATE and moved occurrences
/// (RECURRENCE-ID), tasks as VTODO. Imported UIDs are remembered per list, so importing again updates.
/// </summary>
internal sealed class ICalendarService(IListItemStore items, CalendarDbContext db)
{
    public const string MediaType = "text/calendar";
    public const int MaxExportedItems = 1000;
    public const int MaxImportedComponents = 2000;
    private const string UidSuffix = "@paperdotnet";

    /// <summary>A calendar with the events and tasks of <paramref name="lists"/>. The error is an item query the lists rejected.</summary>
    public async Task<(string? Text, string? Error)> ExportAsync(IReadOnlyList<ListData> lists, CancellationToken ct)
    {
        var eventLists = lists.Where(l => l.ContentTypeKeys.Contains(CalendarService.EventKey)).ToList();
        var taskLists = lists.Where(l => l.ContentTypeKeys.Contains(CalendarService.TaskKey)).ToList();
        var (events, eventError) = await items.QueryAsync(eventLists, new ListItemQuery(null, "fields/start desc", MaxExportedItems), ct);
        if (eventError is not null)
        {
            return (null, eventError);
        }

        var (tasks, taskError) = await items.QueryAsync(taskLists, new ListItemQuery(null, "fields/dueDate desc", MaxExportedItems), ct);
        if (taskError is not null)
        {
            return (null, taskError);
        }

        var eventsByList = events.ToDictionary(page => page.List.Id);
        var tasksByList = tasks.ToDictionary(page => page.List.Id);
        var calendar = new IcalCalendar { ProductId = "-//PaperDotNet//Calendar//EN" };
        var zones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            if (eventsByList.TryGetValue(list.Id, out var page))
            {
                await ExportEventsAsync(page.Items, calendar, zones, ct);
            }

            if (tasksByList.TryGetValue(list.Id, out var due))
            {
                foreach (var task in due.Items)
                {
                    calendar.Todos.Add(Todo(task));
                }
            }
        }

        foreach (var zone in zones.Where(z => z != "UTC"))
        {
            calendar.AddTimeZone(zone);
        }

        return (new CalendarSerializer(calendar).SerializeToString() ?? string.Empty, null);
    }

    private async Task ExportEventsAsync(IReadOnlyList<ListItemData> events, IcalCalendar calendar, HashSet<string> zones, CancellationToken ct)
    {
        var ids = events.Select(e => e.Id).ToList();
        var recurrences = await db.Recurrences.AsNoTracking().Where(r => ids.Contains(r.ItemId)).ToDictionaryAsync(r => r.ItemId, ct);
        var exceptions = await db.OccurrenceChanges.AsNoTracking().Where(e => ids.Contains(e.MasterItemId)).ToListAsync(ct);
        var uids = await db.Sources.AsNoTracking().Where(s => ids.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, s => s.Uid, ct);
        var overrides = exceptions.Where(e => e.OverrideItemId is not null).ToDictionary(e => e.OverrideItemId!.Value);

        string UidOf(Guid itemId) => uids.TryGetValue(itemId, out var uid) && !uid.Contains('#', StringComparison.Ordinal) ? uid : $"{itemId:N}{UidSuffix}";

        foreach (var item in events)
        {
            if (EventTimes.From(item.Fields) is not { } times)
            {
                continue;
            }

            var master = overrides.GetValueOrDefault(item.Id);
            var zone = recurrences.TryGetValue(master?.MasterItemId ?? item.Id, out var recurrence) ? recurrence.TimeZone : "UTC";
            zones.Add(zone);
            var vevent = new CalendarEvent
            {
                Uid = UidOf(master?.MasterItemId ?? item.Id),
                Summary = CalendarService.Title(item),
                Location = Text(item, "location"),
                Description = Text(item, "description"),
                DtStart = times.AllDay ? new CalDateTime(DateOnly.FromDateTime(times.Start.UtcDateTime)) : CalendarService.Local(times.Start, zone),
                DtEnd = times.AllDay ? new CalDateTime(DateOnly.FromDateTime(times.End.UtcDateTime)) : CalendarService.Local(times.End, zone),
                LastModified = new CalDateTime(item.UpdatedAt.UtcDateTime, "UTC"),
            };

            if (master is not null)
            {
                vevent.RecurrenceIdentifier = new RecurrenceIdentifier(times.AllDay ? new CalDateTime(DateOnly.FromDateTime(master.OriginalStart.UtcDateTime)) : CalendarService.Local(master.OriginalStart, zone), null);
            }
            else if (recurrence is not null)
            {
                vevent.RecurrenceRule = new RecurrencePattern(recurrence.Rule);
                foreach (var cancelled in exceptions.Where(e => e.MasterItemId == item.Id && e.OverrideItemId is null))
                {
                    vevent.ExceptionDates.Add(times.AllDay ? new CalDateTime(DateOnly.FromDateTime(cancelled.OriginalStart.UtcDateTime)) : CalendarService.Local(cancelled.OriginalStart, zone));
                }
            }

            calendar.Events.Add(vevent);
        }
    }

    private static Todo Todo(ListItemData task)
    {
        var todo = new Todo
        {
            Uid = $"{task.Id:N}{UidSuffix}",
            Summary = CalendarService.Title(task),
            Description = Text(task, "description"),
            Status = Text(task, "status") == "completed" ? "COMPLETED" : "NEEDS-ACTION",
        };
        if (Text(task, "dueDate") is { } due && DateOnly.TryParse(due, CultureInfo.InvariantCulture, out var day))
        {
            todo.Due = new CalDateTime(day);
        }

        return todo;
    }

    /// <summary>Imports the VEVENTs of <paramref name="text"/> into an event list, as the caller.</summary>
    public async Task<ImportResult> ImportAsync(ListData list, string text, CancellationToken ct)
    {
        IcalCalendar calendar;
        try
        {
            calendar = IcalCalendar.Load(text) ?? throw new FormatException("Empty calendar.");
        }
#pragma warning disable CA1031 // Any parser failure means the input is not valid iCalendar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ImportResult(0, 0, 0, [$"Not a valid iCalendar file: {ex.Message}"]);
        }

        var events = calendar.Events.Take(MaxImportedComponents).ToList();
        int created = 0, updated = 0, skipped = 0;
        var errors = new List<string>();

        // Series and single events first, then moved occurrences (they need their series).
        foreach (var vevent in events.OrderBy(e => e.RecurrenceIdentifier is null ? 0 : 1))
        {
            var result = await ImportEventAsync(list, vevent, ct);
            switch (result)
            {
                case true:
                    created++;
                    break;
                case false:
                    updated++;
                    break;
                default:
                    skipped++;
                    if (errors.Count < 20)
                    {
                        errors.Add($"Skipped '{vevent.Summary ?? vevent.Uid}'.");
                    }

                    break;
            }
        }

        return new ImportResult(created, updated, skipped, errors);
    }

    /// <summary>True when created, false when updated, null when skipped.</summary>
    private async Task<bool?> ImportEventAsync(ListData list, CalendarEvent vevent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vevent.Uid) || vevent.Uid.Length > 200 || vevent.DtStart is null)
        {
            return null;
        }

        var allDay = !vevent.DtStart.HasTime;
        var start = Instant(vevent.DtStart);
        var end = vevent.DtEnd is { } dtEnd ? Instant(dtEnd) : vevent.Duration is { } duration ? Instant(vevent.DtStart.Add(duration)) : allDay ? start.AddDays(1) : start;
        var fields = new JsonObject
        {
            ["title"] = string.IsNullOrWhiteSpace(vevent.Summary) ? "(no title)" : vevent.Summary,
            ["start"] = FieldFormats.DateTime(start),
            ["end"] = FieldFormats.DateTime(end),
            ["allDay"] = allDay,
            ["location"] = vevent.Location,
            ["description"] = vevent.Description,
        };

        EventRecurrence? masterRecurrence = null;
        EventSource? masterSource = null;
        var key = vevent.Uid;
        if (vevent.RecurrenceIdentifier?.StartTime is { } recurrenceId)
        {
            masterSource = await db.Sources.FirstOrDefaultAsync(s => s.ListId == list.Id && s.Uid == vevent.Uid, ct);
            masterRecurrence = masterSource is null ? null : await db.Recurrences.FirstOrDefaultAsync(r => r.ItemId == masterSource.ItemId, ct);
            if (masterRecurrence is null)
            {
                return null;
            }

            key = $"{vevent.Uid}#{Instant(recurrenceId):O}";
        }

        var source = await db.Sources.FirstOrDefaultAsync(s => s.ListId == list.Id && s.Uid == key, ct);
        ListItemData item;
        var isNew = source is null || await items.GetAsync(list.WorkspaceId, list.Id, source.ItemId, ct) is null;
        if (isNew)
        {
            var result = await items.CreateAsync(list.WorkspaceId, list.Id, fields, null, ct);
            if (!result.Succeeded)
            {
                return null;
            }

            item = result.Item!;
            if (source is null)
            {
                source = new EventSource { Id = Ids.New(), ListId = list.Id, Uid = key, ItemId = item.Id };
                db.Sources.Add(source);
            }

            source.ItemId = item.Id;
        }
        else
        {
            var result = await items.UpdateAsync(list.WorkspaceId, list.Id, source!.ItemId, fields, null, ct);
            if (!result.Succeeded)
            {
                return null;
            }

            item = result.Item!;
        }

        if (vevent.RecurrenceIdentifier?.StartTime is { } original)
        {
            var originalStart = Instant(original);
            var exception = await db.OccurrenceChanges.FirstOrDefaultAsync(e => e.MasterItemId == masterSource!.ItemId && e.OriginalStart == originalStart, ct);
            if (exception is null)
            {
                db.OccurrenceChanges.Add(new OccurrenceChange { Id = Ids.New(), MasterItemId = masterSource!.ItemId, OriginalStart = originalStart, OverrideItemId = item.Id });
            }
            else
            {
                exception.OverrideItemId = item.Id;
            }
        }
        else
        {
            await ImportRecurrenceAsync(list, item, vevent, ct);
        }

        await db.SaveChangesAsync(ct);
        return isNew;
    }

    private async Task ImportRecurrenceAsync(ListData list, ListItemData item, CalendarEvent vevent, CancellationToken ct)
    {
        var recurrence = await db.Recurrences.FirstOrDefaultAsync(r => r.ItemId == item.Id, ct);
        if (vevent.RecurrenceRule is null)
        {
            if (recurrence is not null)
            {
                db.Recurrences.Remove(recurrence);
            }

            return;
        }

        var zone = vevent.DtStart!.TzId is { } tz && CalendarService.IsKnownTimeZone(tz) ? tz : "UTC";
        if (recurrence is null)
        {
            recurrence = new EventRecurrence { Id = Ids.New(), ItemId = item.Id, WorkspaceId = list.WorkspaceId, ListId = list.Id, Rule = vevent.RecurrenceRule.ToString() ?? string.Empty, TimeZone = zone };
            db.Recurrences.Add(recurrence);
        }

        recurrence.Rule = vevent.RecurrenceRule.ToString() ?? string.Empty;
        recurrence.TimeZone = zone;
        var existing = await db.OccurrenceChanges.Where(e => e.MasterItemId == item.Id && e.OverrideItemId == null).Select(e => e.OriginalStart).ToListAsync(ct);
        foreach (var date in vevent.ExceptionDates.GetAllDates().Select(Instant).Where(d => !existing.Contains(d)).Distinct())
        {
            db.OccurrenceChanges.Add(new OccurrenceChange { Id = Ids.New(), MasterItemId = item.Id, OriginalStart = date });
        }
    }

    /// <summary>A UTC instant: all-day values are midnight UTC of the date.</summary>
    private static DateTimeOffset Instant(CalDateTime value) => value.HasTime
        ? new DateTimeOffset(value.AsUtc, TimeSpan.Zero)
        : new DateTimeOffset(value.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static string? Text(ListItemData item, string field) => item.Fields[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
