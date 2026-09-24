using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Calendar.Features;

/// <summary>The event content type and the Calendar list template (CAL-01), registered like built-in ones.</summary>
public static class CalendarTemplates
{
    public const string ListTemplateKey = "calendar";

    private static FieldDefinition Field(string name, string displayName, string type, Action<FieldDefinition>? configure = null)
    {
        var field = new FieldDefinition { Name = name, DisplayName = displayName, Type = type };
        configure?.Invoke(field);
        return field;
    }

    public static readonly ContentTypeTemplate ContentType = new(CalendarService.EventKey, "Event", "A calendar event with attendees and a reminder.",
    [
        Field("start", "Start", "dateTime", f => f.Required = true),
        Field("end", "End", "dateTime"),
        Field("allDay", "All day", "boolean"),
        Field("location", "Location", "text"),
        Field("attendees", "Attendees", "person", f => f.AllowMultiple = true),
        Field("reminderMinutes", "Reminder (minutes before)", "number", f => { f.Minimum = 0; f.Maximum = 40320; }),
        Field("description", "Description", "note"),
    ]);

    public static readonly ListTemplateDefinition List = new(ListTemplateKey, "Calendar", "Events on a calendar.", [CalendarService.EventKey],
    [
        new ViewTemplate("Calendar", ["title", "start", "end", "allDay", "location"], OrderBy: "fields/start", GroupBy: "start", Layout: "calendar", IsDefault: true),
        new ViewTemplate("All events", ["title", "start", "end", "location"], OrderBy: "fields/start desc"),
    ]);
}

/// <summary>
/// Keeps event times consistent (before add/update): all-day events start at midnight (UTC) and last
/// whole days; a missing end becomes start + 1 hour (or + 1 day); an end before the start is rejected.
/// </summary>
internal sealed class EventTimesReceiver : IItemEventReceiver
{
    public int Sequence => 50;

    public bool AppliesTo(ItemEventScope scope) => !scope.IsFolder && scope.ContentTypeKey == CalendarService.EventKey;

    public ValueTask ItemAddingAsync(ItemChangingContext context, CancellationToken cancellationToken) => ApplyAsync(context);

    public ValueTask ItemUpdatingAsync(ItemChangingContext context, CancellationToken cancellationToken) => ApplyAsync(context);

    private static ValueTask ApplyAsync(ItemChangingContext context)
    {
        var fields = context.After;
        if (fields is null || !EventTimes.TryParse(fields["start"], out var start))
        {
            return ValueTask.CompletedTask;
        }

        var allDay = fields["allDay"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        var hasEnd = EventTimes.TryParse(fields["end"], out var end);
        if (allDay)
        {
            start = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero);
            end = hasEnd ? new DateTimeOffset(end.UtcDateTime.Date, TimeSpan.Zero) : start;
            end = end <= start ? start.AddDays(1) : end;
        }
        else if (!hasEnd)
        {
            end = start.AddHours(1);
        }

        if (end < start)
        {
            context.Cancel("The end of an event must not be before its start.");
            return ValueTask.CompletedTask;
        }

        fields["start"] = FieldFormats.DateTime(start);
        fields["end"] = FieldFormats.DateTime(end);
        return ValueTask.CompletedTask;
    }
}
