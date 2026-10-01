using System.Globalization;
using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;

namespace PaperDotNet.Tasks.Features;

/// <summary>
/// Due-task reminders (NTF-02): assignees of open tasks due today (UTC) get one reminder per task and day
/// (deduplication key). Runs every 15 minutes, so tasks added later in the day are covered too.
/// </summary>
internal sealed class DueTaskReminderJob(IListItemStore items, INotificationSender sender, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "tasks.reminders";
    public const string Schedule = "*/15 * * * *";

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var system = items.AsSystem(new ChangeActor(tenantId, null));
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var lists = (await system.GetListsAsync(null, null, cancellationToken)).Where(l => l.ContentTypeKeys.Contains(TaskTemplates.ContentTypeKey)).ToList();
        if (lists.Count == 0)
        {
            return;
        }

        var filter = $"fields/status ne '{TaskTemplates.Completed}' and fields/dueDate eq {today}";
        var (pages, error) = await system.QueryAsync(lists, new ListItemQuery(filter, null, ListItemQuery.MaxTop), cancellationToken);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        foreach (var page in pages)
        {
            foreach (var task in page.Items)
            {
                if (task.Fields["assignedTo"] is not JsonArray { Count: > 0 } assigned)
                {
                    continue;
                }

                await sender.SendAsync(tenantId,
                    new NotificationMessage(NotificationTypes.Reminder, $"Due today: {task.Fields["title"]?.GetValue<string>()}", $"In {page.List.Name}.",
                        new NotificationLink(task.WorkspaceId, task.ListId, task.Id), $"reminder:task:{task.Id:N}:{today}"),
                    [.. assigned.Select(a => Guid.Parse(a!.GetValue<string>()))], cancellationToken);
            }
        }
    }
}
