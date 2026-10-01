using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Tasks.Features;

/// <summary>
/// Raises the workflow trigger <c>task.completed</c> (ADR-0036) when a task's status becomes completed. The trigger is the
/// item event again (an id made from it and its causation depth), so a redelivered event starts nothing twice and loops
/// end. A Wolverine handler generated ahead of time.
/// </summary>
public static class TaskCompletedSubscriber
{
    public static async Task Handle(ItemUpdated e, IListItemStore items, IWorkflowTriggers triggers, CancellationToken cancellationToken)
    {
        if (e.IsFolder || !e.ChangedFields.Contains("status"))
        {
            return;
        }

        var store = items.AsSystem(new ChangeActor(e.TenantId, null, e.Depth));
        var list = await store.GetListAsync(e.WorkspaceId, e.ListId, cancellationToken);
        if (list?.ContentTypes.FirstOrDefault(c => c.Id == e.ContentTypeId)?.Key != TaskTemplates.ContentTypeKey)
        {
            return;
        }

        var task = await store.GetAsync(e.WorkspaceId, e.ListId, e.ItemId, cancellationToken);
        if (task?.Fields["status"] is not JsonValue status || !status.TryGetValue<string>(out var value) || value != TaskTemplates.Completed)
        {
            return;
        }

        await triggers.RaiseAsync(
            WorkflowTriggerKeys.TaskCompleted, e.WorkspaceId, new WorkflowItem(e.WorkspaceId, e.ListId, e.ItemId),
            new JsonObject { ["completedBy"] = e.UserId?.ToString() }, e, cancellationToken);
    }
}
