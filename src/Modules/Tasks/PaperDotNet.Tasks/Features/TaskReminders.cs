using System.Globalization;
using System.Text.Json.Nodes;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;

namespace PaperDotNet.Tasks.Features;

/// <summary>
/// Due-task reminders (NTF-02): assignees of open tasks due today (UTC) get one reminder per task and
/// day (deduplication key). Runs every 15 minutes, so tasks added later in the day are covered too.
/// </summary>
internal sealed class DueTaskReminderJob(IListItemStore items, INotificationSender sender, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "tasks.reminders";
    public const string Schedule = "*/15 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var system = items.AsSystem();
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filter = $"fields/status ne '{TaskTemplates.Completed}' and fields/dueDate eq {today}";
        foreach (var list in (await system.GetListsAsync(null, null, cancellationToken)).Where(l => l.ContentTypeKeys.Contains(TaskTemplates.ContentTypeKey)))
        {
            var (due, _) = await system.QueryAsync(list.WorkspaceId, list.Id, new ListItemQuery(filter, null, 1000), cancellationToken);
            foreach (var task in due)
            {
                if (task.Fields["assignedTo"] is not JsonArray { Count: > 0 } assigned)
                {
                    continue;
                }

                await sender.SendAsync(
                    new NotificationMessage(NotificationTypes.Reminder, $"Due today: {task.Fields["title"]?.GetValue<string>()}", $"In {list.Name}.",
                        new NotificationLink(task.WorkspaceId, task.ListId, task.Id), $"reminder:task:{task.Id:N}:{today}"),
                    [.. assigned.Select(a => Guid.Parse(a!.GetValue<string>()))], cancellationToken);
            }
        }
    }
}
