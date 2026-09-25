using PaperDotNet.Calendar.Data;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;

namespace PaperDotNet.Calendar.Features;

/// <summary>
/// Event reminders (NTF-02): once an occurrence is within its <c>reminderMinutes</c>, its attendees (or,
/// without attendees, its creator) get a reminder. Once per occurrence (deduplication key); runs every minute.
/// </summary>
internal sealed class EventReminderJob(IListItemStore items, CalendarDbContext db, INotificationSender sender, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "calendar.reminders";
    public const string Schedule = "* * * * *";

    /// <summary>The largest reminder offset (the field's maximum, 28 days).</summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromMinutes(40320);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var calendar = new CalendarService(items.AsSystem(), db);
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
            await sender.SendAsync(
                new NotificationMessage(NotificationTypes.Reminder, $"{entry.Title ?? "Event"} starts {entry.Start:yyyy-MM-dd HH:mm} UTC", entry.Location,
                    new NotificationLink(entry.WorkspaceId, entry.ListId, entry.ItemId), $"reminder:event:{entry.ItemId:N}:{entry.Start.UtcTicks}"),
                recipients, cancellationToken);
        }
    }
}
