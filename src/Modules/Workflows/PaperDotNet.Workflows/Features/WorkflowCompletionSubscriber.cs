using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Connects registered trigger completions to durable waits (<see cref="WorkflowTriggerDefinition.CompletionKind"/>),
/// including completions that arrive before the wait. The wait completes once every run the request started has ended,
/// so a coordinator resumes once per request, never while a sibling run is still going.
/// </summary>
internal sealed class WorkflowCompletionSubscriber(TriggerCatalog triggers, IWorkflowBookmarks bookmarks, WorkflowsDbContext db) : IEventSubscriber<WorkflowRunFinished>
{
    public async Task HandleAsync(WorkflowRunFinished finished, CancellationToken ct)
    {
        var kind = triggers.All.FirstOrDefault(t => t.Key == finished.Trigger)?.CompletionKind;
        if (kind is null || finished.CauseEventId is not { } request)
        {
            return;
        }

        var runs = await db.Runs.AsNoTracking().Where(r => r.EventId == request).Select(r => new { r.Id, r.Status, r.Error }).ToListAsync(ct);
        if (runs.Any(r => r.Status is RunStatus.Running or RunStatus.Waiting))
        {
            return;
        }

        var status = runs.Any(r => r.Status == RunStatus.Failed) ? "failed"
            : runs.Any(r => r.Status == RunStatus.Cancelled) ? "cancelled"
            : runs.Count == 0 ? "none" : "completed";
        await bookmarks.CompleteAsync(kind, request.ToString(), new JsonObject
        {
            ["status"] = status,
            ["runs"] = new JsonArray([.. runs.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id.ToString(),
                ["status"] = r.Status.ToString().ToLowerInvariant(),
                ["error"] = r.Error,
            })]),
        }, ct);
    }
}
