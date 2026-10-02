using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Calendar.Features;

internal static class CalendarSourceWorkflow
{
    public const string Key = "calendar.replicate";
    public static readonly BuiltInWorkflow Definition = new(Key, "Refresh calendar sources",
        "Refreshes all active iCalendar URL sources in this workspace every 15 minutes.",
        JsonNode.Parse("""
        {
          "trigger": { "type": "schedule", "cron": "*/15 * * * *", "timeZone": "UTC" },
          "flow": { "start": "refresh", "nodes": { "refresh": { "activity": "calendar.refreshSources", "inputs": {} } } }
        }
        """)!.AsObject());
}

internal sealed class RefreshCalendarSources(CalendarDbContext db, CalendarSourceReplication replication) : IWorkflowActivity
{
    public string Key => "calendar.refreshSources";
    public string Description => "Refreshes active iCalendar sources in this workspace, optionally one source by id.";
    public JsonObject InputSchema => ActivitySchemas.Of([], ("sourceId", ActivitySchemas.Text("Optional source subscription id.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        var sourceText = ActivityInputs.Text(context.Inputs, "sourceId");
        Guid? sourceId = null;
        if (sourceText is not null)
        {
            if (!Guid.TryParse(sourceText, out var parsed))
            {
                return WorkflowActivityResult.Fail("sourceId must be a subscription id.");
            }

            sourceId = parsed;
        }

        var ids = await db.Subscriptions.AsNoTracking().Where(s => s.WorkspaceId == context.WorkspaceId && !s.Paused
            && (sourceId == null || s.Id == sourceId)).Select(s => s.Id).ToListAsync(cancellationToken);
        var failures = 0;
        foreach (var id in ids)
        {
            if (!await replication.RefreshAsync(id, cancellationToken))
            {
                failures++;
            }
        }

        return failures == 0 ? WorkflowActivityResult.Ok(new JsonObject { ["sources"] = ids.Count })
            : WorkflowActivityResult.Fail($"{failures} calendar source(s) could not refresh. See Calendar sources for details.");
    }
}
