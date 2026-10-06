using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>Connects registered trigger completions to durable waits, including completions that arrive before a wait.</summary>
internal sealed class WorkflowCompletionSubscriber(TriggerCatalog triggers, IWorkflowBookmarks bookmarks) : IEventSubscriber<WorkflowRunFinished>
{
    public async Task HandleAsync(WorkflowRunFinished finished, CancellationToken ct)
    {
        var kind = triggers.All.FirstOrDefault(t => t.Key == finished.Trigger)?.CompletionKind;
        if (kind is not null && finished.CauseEventId is { } request)
        {
            await bookmarks.CompleteAsync(kind, request.ToString(),
                new JsonObject { ["runId"] = finished.RunId.ToString(), ["status"] = finished.Status, ["error"] = finished.Error }, ct);
        }
    }
}
