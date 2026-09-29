using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Tasks.Features;

/// <summary>
/// Raises the workflow trigger <c>task.completed</c> (ADR-0036) when a task's status becomes completed. The trigger is the
/// item event again (its id and causation depth), so a redelivered event starts nothing twice and loops end.
/// </summary>
internal sealed class TaskCompletedTrigger(IListItemStore items, IWorkflowTriggers triggers) : IEventSubscriber<ItemUpdated>
{
    public async Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken)
    {
        if (integrationEvent.IsFolder || !integrationEvent.ChangedFields.Contains("status"))
        {
            return;
        }

        var store = items.AsSystem();
        var list = await store.GetListAsync(integrationEvent.WorkspaceId, integrationEvent.ListId, cancellationToken);
        if (list?.ContentTypes.FirstOrDefault(c => c.Id == integrationEvent.ContentTypeId)?.Key != TaskTemplates.ContentTypeKey)
        {
            return;
        }

        var task = await store.GetAsync(integrationEvent.WorkspaceId, integrationEvent.ListId, integrationEvent.ItemId, cancellationToken);
        if (task?.Fields["status"]?.GetValue<string>() != TaskTemplates.Completed)
        {
            return;
        }

        await triggers.RaiseAsync(
            WorkflowTriggers.TaskCompleted, integrationEvent.WorkspaceId, new WorkflowItem(integrationEvent.WorkspaceId, integrationEvent.ListId, integrationEvent.ItemId),
            new JsonObject { ["completedBy"] = integrationEvent.UserId?.ToString() }, integrationEvent, cancellationToken);
    }
}
