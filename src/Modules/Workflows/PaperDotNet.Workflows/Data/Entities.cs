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

    /// <summary>
    /// The stable key of the workflow's events (<c>wf.{key}.completed</c>, ADR-0038): made from the name when it is
    /// created, unchanged by a rename. Null for workflows saved before keys (see <see cref="EventKey"/>).
    /// </summary>
    public string? Key { get; set; }

    public string? Description { get; set; }

    public bool Enabled { get; set; }

    /// <summary>Number of the version new runs use.</summary>
    public int CurrentVersion { get; set; }

    /// <summary>
    /// The built-in workflow this is (EVT-12): read-only, its definition comes from the release with <see cref="Parameters"/>
    /// filled in; null for workflows people wrote.
    /// </summary>
    public string? BuiltInKey { get; set; }

    /// <summary>The values of a built-in workflow's parameters as a JSON object.</summary>
    public string? Parameters { get; set; }

    /// <summary>The built-in workflow this one was copied from, if any.</summary>
    public string? CopiedFrom { get; set; }

    /// <summary>The library a per-library built-in workflow belongs to (its triggers apply to that list only); null otherwise.</summary>
    public Guid? ListId { get; set; }

    /// <summary>The trigger types of the current version, as <c>,type,type,</c> (found by <c>,type,</c>).</summary>
    public string TriggerTypes { get; set; } = ",";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }

    /// <summary>The key its events use: <see cref="Key"/>, else one made from the name.</summary>
    public string EventKey => Key ?? BuiltInKey ?? Features.WorkflowKeys.FromName(Name);
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

    /// <summary>Waiting for a bookmark (an approval, a delay, or something another module completes).</summary>
    public const string Waiting = "waiting";
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

    /// <summary>The bookmark a waiting run waits on.</summary>
    public Guid? WaitingOn { get; set; }

    /// <summary>Failed tries of the current node so far (its retry policy decides whether it runs again).</summary>
    public int NodeAttempts { get; set; }

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

    /// <summary><see cref="CompletedAt"/> in Unix milliseconds (cleanup compares it in SQL, ADR-0039).</summary>
    public long? CompletedAtUnixMs { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>Kinds of bookmarks the engine creates itself; other modules complete bookmarks of their own kinds.</summary>
public static class BookmarkKinds
{
    public const string Approval = "approval";
    public const string Delay = "delay";

    /// <summary>A failed node waits before it runs again (its retry policy).</summary>
    public const string Retry = "retry";

    public static readonly string[] Reserved = [Approval, Delay, Retry];
}

/// <summary>
/// A durable wait of a run (ADR-0036): an approval, a delay, or anything another module completes. It is completed with
/// a payload, together with the message that resumes the run; bookmarks with a resume time are completed by the minute
/// job when their time has come. A completion that arrives before any run waits for it has no run yet (<see cref="RunId"/>
/// empty) and is taken over by the run that starts waiting for it.
/// </summary>
public class WorkflowBookmark : ITenantOwned, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RunId { get; set; }

    /// <summary>The node that waits.</summary>
    public string Node { get; set; } = "";

    public string Kind { get; set; } = "";

    /// <summary>What completes it, unique per kind in the tenant (e.g. the approval id).</summary>
    public string Key { get; set; } = "";

    /// <summary>When the wait ends by itself (Unix milliseconds: SQLite compares numbers, not dates, ADR-0039).</summary>
    public long? ResumeAtUnixMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long CreatedAtUnixMs { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long? CompletedAtUnixMs { get; set; }

    /// <summary>What completed it as a JSON object; becomes the node's output (its <c>outcome</c> picks the port).</summary>
    public string? Payload { get; set; }

    /// <summary>What the wait is about as a JSON object, from the activity.</summary>
    public string? Data { get; set; }

    /// <summary>When completed, the node runs again instead of taking the payload as its output.</summary>
    public bool RunAgain { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>States of an approval request (text, ADR-0039).</summary>
public static class ApprovalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Pending, Approved, Rejected, Cancelled];
}

/// <summary>An approval node waiting for a decision of one of its assignees.</summary>
public class ApprovalRequest : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RunId { get; set; }

    /// <summary>The approval node.</summary>
    public string Node { get; set; } = "";

    public Guid WorkspaceId { get; set; }

    public Guid? ListId { get; set; }

    public Guid? ItemId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Who may decide, as a JSON array of user ids (found with <c>Contains("\"id\"")</c>).</summary>
    public string Assignees { get; set; } = "[]";

    /// <summary>Added as assignees (and notified) when the request is overdue, as a JSON array of user ids.</summary>
    public string EscalateTo { get; set; } = "[]";

    public DateTimeOffset? DueAt { get; set; }

    public long? DueAtUnixMs { get; set; }

    public bool Escalated { get; set; }

    public string Status { get; set; } = ApprovalStatus.Pending;

    public Guid? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>
/// Where the schedule job is with a timed trigger (<c>schedule</c> or <c>date</c>) of a workflow. A row belongs to one
/// version of the workflow; a new version starts over. Times are Unix milliseconds (ADR-0039).
/// </summary>
public class WorkflowSchedule : ITenantOwned, IVersioned
{
    /// <summary>Made from the workflow and the trigger's position.</summary>
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The workflow version the row was computed for.</summary>
    public int WorkflowVersion { get; set; }

    /// <summary>The next occurrence of a schedule.</summary>
    public long? NextAtUnixMs { get; set; }

    /// <summary>Up to when the trigger was checked.</summary>
    public long? CheckedUntilUnixMs { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

#pragma warning restore CA1852
