using System.ComponentModel.DataAnnotations;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Workflows.Data;

#pragma warning disable CA1852 // Not sealed: EF Core precompiled materializers (ADR-0039).

/// <summary>A workflow (ADR-0036): its name, whether it runs, and its current version.</summary>
public class WorkflowDefinition : ITenantOwned, IVersioned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The workspace whose lists the workflow reacts to and changes.</summary>
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public bool Enabled { get; set; }

    /// <summary>Number of the version new runs use.</summary>
    public int CurrentVersion { get; set; }

    /// <summary>The trigger types of the current version, as <c>,type,type,</c> (found by <c>,type,</c>).</summary>
    public string TriggerTypes { get; set; } = ",";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>A saved definition of a workflow: runs keep the version they started with.</summary>
public class WorkflowVersion : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkflowId { get; set; }

    public int Number { get; set; }

    /// <summary>The definition as JSON (<see cref="Features.WorkflowSpec"/>).</summary>
    public string Definition { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>States of a run (stored as text: EF Core's compiled model reflects over enums, ADR-0039).</summary>
public static class RunStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>A run of a workflow: where it is in the flow and its state (outputs of the nodes that ran, variables, log).</summary>
public class WorkflowRun : ITenantOwned, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid WorkflowId { get; set; }

    public int WorkflowVersion { get; set; }

    public string Status { get; set; } = RunStatus.Running;

    /// <summary>The node to run next (null before the start node).</summary>
    public string? Node { get; set; }

    /// <summary>Id of the current node's execution, saved before it runs, so a repeat after a crash is recognized.</summary>
    public Guid? StepExecutionId { get; set; }

    /// <summary>Nodes run so far (loop guard).</summary>
    public int NodesRun { get; set; }

    public string Trigger { get; set; } = "";

    public Guid? ListId { get; set; }

    public Guid? ItemId { get; set; }

    /// <summary>Trigger data (JSON object), e.g. the title of a deleted item.</summary>
    public string? Data { get; set; }

    public string Outputs { get; set; } = "{}";

    public string Variables { get; set; } = "{}";

    public string Log { get; set; } = "[]";

    public string? Error { get; set; }

    public string? FailedNode { get; set; }

    public Guid? StartedBy { get; set; }

    /// <summary>Causation depth of the change that started the run; the run's own changes are one deeper.</summary>
    public int Depth { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

#pragma warning restore CA1852
