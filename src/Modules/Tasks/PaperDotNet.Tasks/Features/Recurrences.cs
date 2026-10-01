using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Tasks.Data;

namespace PaperDotNet.Tasks.Features;

/// <summary>RRULE handling with Ical.Net (RFC 5545).</summary>
internal static class Recurrences
{
    public static bool IsValid(string rule)
    {
        try
        {
            _ = new RecurrencePattern(rule);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The first occurrence after <paramref name="after"/> (a series that starts on it), or null when the rule has ended.</summary>
    public static DateOnly? Next(string rule, DateOnly? after)
    {
        if (after is not { } start)
        {
            return null;
        }

        var series = new CalendarEvent { DtStart = new CalDateTime(start), RecurrenceRule = new RecurrencePattern(rule) };
        return series.GetOccurrences(null, null)
            .Select(o => o.Period.StartTime.Date)
            .SkipWhile(d => d <= start)
            .Take(1)
            .Cast<DateOnly?>()
            .FirstOrDefault();
    }
}

/// <summary>
/// Completing a repeating task creates its next occurrence (TSK-05): same title, priority, assignees, description and
/// checklist (unchecked), due on the next date of the rule; the rule moves to the new task. Idempotent: the next task's
/// id is derived from the rule and the completed task, its checklist is saved before the task is created (so it never
/// appears without it), and a repeated event finds the task created before; once the rule moved, a redelivered event
/// finds nothing to do. Permanently deleted items lose their checklists, links and recurrences. A Wolverine handler
/// generated ahead of time.
/// </summary>
public static class TaskRecurrenceSubscriber
{
    private static readonly string[] CopiedFields = ["title", "priority", "assignedTo", "description"];

    public static async Task Handle(ItemUpdated e, TasksDbContext database, IListItemStore items, CancellationToken cancellationToken)
    {
        if (!e.ChangedFields.Contains("status"))
        {
            return;
        }

        var db = database;
        var tenant = e.TenantId;
        var itemId = e.ItemId;
        var ct = cancellationToken;
        var recurrence = await db.Recurrences.FirstOrDefaultAsync(r => r.TenantId == tenant && r.ItemId == itemId, ct);
        if (recurrence is null)
        {
            return;
        }

        // Changes made in reaction to an event carry its depth + 1 (loop protection).
        var system = items.AsSystem(new ChangeActor(tenant, e.UserId, e.Depth + 1));
        var task = await system.GetAsync(recurrence.WorkspaceId, recurrence.ListId, recurrence.ItemId, ct);
        if (task?.Fields["status"]?.GetValue<string>() != TaskTemplates.Completed)
        {
            return;
        }

        var due = TaskEndpoints.DueDate(task);
        var next = Recurrences.Next(recurrence.Rule, due);
        if (next is null)
        {
            db.Recurrences.Remove(recurrence); // The series has ended.
            await db.SaveChangesAsync(ct);
            return;
        }

        var fields = new JsonObject();
        foreach (var name in CopiedFields.Where(n => task.Fields[n] is not null))
        {
            fields[name] = task.Fields[name]!.DeepClone();
        }

        fields["dueDate"] = Format(next.Value);
        if (task.Fields["startDate"] is JsonValue start && DateOnly.TryParse(start.GetValue<string>(), CultureInfo.InvariantCulture, out var startDate))
        {
            fields["startDate"] = Format(startDate.AddDays(next.Value.DayNumber - due!.Value.DayNumber));
        }

        var nextId = NextItemId(recurrence.Id, task.Id);
        var previousId = task.Id;
        if (!await db.Checklist.AnyAsync(c => c.TenantId == tenant && c.ItemId == nextId, ct))
        {
            var checklist = await db.Checklist.AsNoTracking().Where(c => c.TenantId == tenant && c.ItemId == previousId).OrderBy(c => c.Position).ToListAsync(ct);
            db.Checklist.AddRange(checklist.Select(c => new ChecklistEntry { Id = Ids.New(), TenantId = tenant, ItemId = nextId, Position = c.Position, Text = c.Text }));
            await db.SaveChangesAsync(ct);
        }

        var created = await system.CreateAsync(task.WorkspaceId, task.ListId, nextId, fields, task.ContentTypeId, ct);
        if (!created.Succeeded)
        {
            return;
        }

        recurrence.ItemId = nextId;
        await db.SaveChangesAsync(ct);
    }

    public static async Task Handle(ItemPurged e, TasksDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = e.TenantId;
        var id = e.ItemId;
        var ct = cancellationToken;
        db.Checklist.RemoveRange(await db.Checklist.Where(c => c.TenantId == tenant && c.ItemId == id).ToListAsync(ct));
        db.Links.RemoveRange(await db.Links.Where(l => l.TenantId == tenant && (l.ItemId == id || l.TargetItemId == id)).ToListAsync(ct));
        db.Recurrences.RemoveRange(await db.Recurrences.Where(r => r.TenantId == tenant && r.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The id of the occurrence after <paramref name="previous"/>: the same every time, so repeats find it.</summary>
    internal static Guid NextItemId(Guid recurrence, Guid previous)
    {
        Span<byte> input = stackalloc byte[32];
        recurrence.TryWriteBytes(input);
        previous.TryWriteBytes(input[16..]);
        var bytes = SHA256.HashData(input)[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80); // Version 8 (name-based, custom).
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant.
        return new Guid(bytes);
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
