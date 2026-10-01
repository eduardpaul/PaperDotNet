using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>
/// A trigger fired for a workspace (ADR-0036, ADR-0038): a workflow event (<c>wf.{key}.{event}</c>) or a module or
/// extension trigger. Starts the enabled workflows of the workspace with that trigger type, on the item if there is one.
/// <see cref="Data"/> is a JSON object (the run's <c>{trigger:name}</c> values). Its <see cref="IntegrationEvent.EventId"/>
/// identifies it: the same event starts nothing twice.
/// </summary>
public sealed record WorkflowTriggerRaised : IntegrationEvent
{
    public required string Trigger { get; init; }

    public required Guid WorkspaceId { get; init; }

    public Guid? ListId { get; init; }

    public Guid? ItemId { get; init; }

    public string? Data { get; init; }
}

/// <summary>The item a workflow trigger is about.</summary>
public sealed record WorkflowItem(Guid WorkspaceId, Guid ListId, Guid ItemId);

/// <summary>A trigger a module or extension offers to workflows (an extension's key starts with its id).</summary>
public sealed record WorkflowTriggerDefinition(string Key, string Description);

/// <summary>Keys of the triggers other modules raise (ADR-0036).</summary>
public static class WorkflowTriggerKeys
{
    /// <summary>A task was completed (raised by Tasks; data: <c>completedBy</c>).</summary>
    public const string TaskCompleted = "task.completed";

    /// <summary>A comment was added to an item (raised by Collaboration; data: <c>commentId</c>, <c>text</c>, <c>author</c>, <c>reply</c>).</summary>
    public const string CommentAdded = "comment.added";

    /// <summary>An approval of a workflow run was decided (data: <c>workflow</c>, <c>step</c>, <c>outcome</c>, <c>comment</c>, <c>decidedBy</c>).</summary>
    public const string ApprovalDecided = "approval.decided";
}

/// <summary>
/// Starts the workflows of a trigger in a workspace (in the background, like item events). The tenant is named through
/// the actor or the causing event: there is no ambient tenant under Native AOT (ADR-0039).
/// </summary>
public interface IWorkflowTriggers
{
    /// <summary>
    /// Raises the trigger as the event <paramref name="eventId"/>: raising it again with the same id starts nothing new
    /// (use an id of what caused it, so a repeat is harmless).
    /// </summary>
    Task RaiseAsync(ChangeActor actor, string triggerKey, Guid workspaceId, WorkflowItem? item, JsonObject? data, Guid eventId, CancellationToken cancellationToken);

    /// <summary>
    /// Raises the trigger for the event a subscriber handles (e.g. an item event): its tenant, user and causation depth, and
    /// an id made from it (a redelivery starts nothing new; the loop protection keeps counting).
    /// </summary>
    Task RaiseAsync(string triggerKey, Guid workspaceId, WorkflowItem? item, JsonObject? data, IntegrationEvent cause, CancellationToken cancellationToken);
}
