using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;

namespace PaperDotNet.Calendar.Features;

/// <summary>
/// Event reminders (NTF-02): once an occurrence is within its <c>reminderMinutes</c>, its attendees (or, without
/// attendees, its creator) get a reminder. Once per occurrence (deduplication key); runs every minute.
/// </summary>
internal sealed class EventReminderJob(IListItemStore items, CalendarDbContext db, INotificationSender sender, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "calendar.reminders";
    public const string Schedule = "* * * * *";

    /// <summary>The largest reminder offset (the field's maximum, 28 days).</summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromMinutes(40320);

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var calendar = new CalendarService(items.AsSystem(new ChangeActor(tenantId, null)), db, tenantId);
        var lists = (await calendar.ListsAsync(null, null, cancellationToken)).Where(l => l.ContentTypeKeys.Contains(CalendarService.EventKey)).ToList();
        if (lists.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        var (entries, error) = await calendar.RangeAsync(lists, now, now + Horizon, includeTasks: false, cancellationToken);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        foreach (var entry in entries)
        {
            if (entry.ReminderMinutes is not { } minutes || entry.Start - TimeSpan.FromMinutes(minutes) > now || entry.Start <= now)
            {
                continue;
            }

            var recipients = entry.Attendees.Count > 0 ? entry.Attendees : entry.CreatedBy is { } creator ? [creator] : [];
            await sender.SendAsync(tenantId,
                new NotificationMessage(NotificationTypes.Reminder, $"{entry.Title ?? "Event"} starts {entry.Start:yyyy-MM-dd HH:mm} UTC", entry.Location,
                    new NotificationLink(entry.WorkspaceId, entry.ListId, entry.ItemId), $"reminder:event:{entry.ItemId:N}:{entry.Start.UtcTicks}"),
                recipients, cancellationToken);
        }
    }
}

/// <summary>Removes the series, exceptions and import sources of permanently deleted events (a Wolverine handler).</summary>
public static class CalendarCleanupSubscriber
{
    public static async Task Handle(ItemPurged e, CalendarDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = e.TenantId;
        var id = e.ItemId;
        var ct = cancellationToken;
        db.Recurrences.RemoveRange(await db.Recurrences.Where(r => r.TenantId == tenant && r.ItemId == id).ToListAsync(ct));
        db.OccurrenceChanges.RemoveRange(await db.OccurrenceChanges.Where(c => c.TenantId == tenant && c.MasterItemId == id).ToListAsync(ct));
        foreach (var moved in await db.OccurrenceChanges.Where(c => c.TenantId == tenant && c.OverrideItemId == id).ToListAsync(ct))
        {
            moved.OverrideItemId = null;
        }

        db.Sources.RemoveRange(await db.Sources.Where(s => s.TenantId == tenant && s.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
