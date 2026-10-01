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
