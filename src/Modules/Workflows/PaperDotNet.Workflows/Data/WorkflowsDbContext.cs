using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Workflows.Data;

/// <summary>
/// A workflow of a workspace (EVT-07, EVT-08): a trigger, an optional condition and steps. The definition
/// lives in immutable versions; runs keep the version they started with.
/// </summary>
public sealed class WorkflowDefinition : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The stable key of its events (<c>wf.{key}.completed</c>, ADR-0038): made from the name when created, unchanged by a
    /// rename; a built-in workflow's is its built-in key. Null only for workflows saved before keys (see <see cref="EventKey"/>).
    /// </summary>
    public string? Key { get; set; }

    /// <summary>The list a per-library built-in workflow belongs to (its triggers apply to that list only); null otherwise.</summary>
    public Guid? ListId { get; set; }

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Trigger type of the current version, kept as a column to find workflows for an event quickly.</summary>
    public required string Trigger { get; set; }

    /// <summary>The version new runs use.</summary>
    public int CurrentVersion { get; set; }

    /// <summary>
    /// The built-in workflow this is (EVT-12): read-only, its definition comes from the release with
    /// <see cref="Parameters"/> filled in; null for workflows people wrote.
    /// </summary>
    public string? BuiltInKey { get; set; }

    /// <summary>The values of a built-in workflow's parameters as a JSON object.</summary>
    public string? Parameters { get; set; }

    /// <summary>The built-in workflow this one was copied from, if any.</summary>
    public string? CopiedFrom { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }

    /// <summary>The key its events use: <see cref="Key"/>, else the built-in key, else one made from the name.</summary>
    public string EventKey => Key ?? BuiltInKey ?? WorkflowKeys.FromName(Name);
}

/// <summary>Keys of workflows (ADR-0038).</summary>
public static partial class WorkflowKeys
{
    public const int MaxLength = 100;

    /// <summary>A key made from a name: lower case letters, digits and dashes, e.g. <c>check-big-bills</c>.</summary>
    public static string FromName(string name)
    {
        var key = NotKey().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        key = key.Length > MaxLength ? key[..MaxLength].TrimEnd('-') : key;
        return key.Length == 0 ? "workflow" : key;
    }

    /// <summary>Whether a key people chose is valid: lower case letters, digits, dashes and underscores (dots are for built-in keys).</summary>
    public static bool IsValid(string key) => key.Length <= MaxLength && Valid().IsMatch(key);

    [System.Text.RegularExpressions.GeneratedRegex("[^a-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex NotKey();

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial System.Text.RegularExpressions.Regex Valid();
}

/// <summary>An immutable version of a workflow's definition.</summary>
[NotAudited]
public sealed class WorkflowVersion : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkflowId { get; set; }

    public int Number { get; set; }

    /// <summary>The <c>WorkflowSpec</c> as JSON (names, never ids, so it is portable).</summary>
    public required string Definition { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}

public enum RunStatus
{
    Running = 0,

    /// <summary>Waiting for an approval or a timer.</summary>
    Waiting = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// A run of a workflow (ADR-0036): the node it is at, its variables and the outputs of the nodes it ran, and a short
/// log. A run started by an event records the event (unique per workflow, so a redelivered event starts nothing). A
/// waiting run waits on one <see cref="WorkflowBookmark"/>.
/// </summary>
[NotAudited]
public sealed class WorkflowRun : ITenantOwned, IVersioned
{
    /// <summary>Effective concurrency at creation, including an optional trigger override.</summary>
    public string? Concurrency { get; set; }

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkflowId { get; set; }

    public int WorkflowVersion { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The item the run works on; null for extension triggers without an item.</summary>
    public Guid? ListId { get; set; }

    public Guid? ItemId { get; set; }

    /// <summary>The event that started the run; null for manual starts.</summary>
    public Guid? EventId { get; set; }

    /// <summary>Data of an extension trigger as a JSON object, if any.</summary>
    public string? Data { get; set; }

    public RunStatus Status { get; set; }

    /// <summary>The node to run next (or being waited on); null before the start node.</summary>
    public string? Node { get; set; }

    /// <summary>The run's variables as a JSON object.</summary>
    public string Variables { get; set; } = "{}";

    /// <summary>Outputs of the nodes that ran, by node id, as a JSON object (an approval's output holds its <c>outcome</c>).</summary>
    public string Outputs { get; set; } = "{}";

    /// <summary>Recent log entries as a JSON array.</summary>
    public string Log { get; set; } = "[]";

    public string? Error { get; set; }

    /// <summary>The node where the run failed; a failed run with a node can be retried from there.</summary>
    public string? FailedNode { get; set; }

    /// <summary>The bookmark a waiting run waits on; resume messages must name it.</summary>
    public Guid? WaitingOn { get; set; }

    /// <summary>Failed tries of the current node so far (its retry policy decides whether it runs again).</summary>
    public int NodeAttempts { get; set; }

    /// <summary>Nodes the run executed in all (a limit ends runs that loop forever).</summary>
    public int Executed { get; set; }

    public int Depth { get; set; }

    /// <summary>Id of the current action's execution (stable across retries of the node).</summary>
    public Guid? StepExecutionId { get; set; }

    /// <summary>The handler executing the run and until when (a crashed handler's lease expires).</summary>
    public Guid? LeaseId { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Executions of the current step so far; reset when the run makes progress.</summary>
    public int Attempts { get; set; }

    /// <summary>When the run last made progress (or was last looked at).</summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>The timer job does not resume the run again before this time.</summary>
    public DateTimeOffset? NextCheckAt { get; set; }

    /// <summary>The user who started the run or whose change triggered it.</summary>
    public Guid? StartedBy { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public uint Version { get; set; }
}

/// <summary>
/// Where the schedule job is with a workflow that has a <c>schedule</c> or <c>date</c> trigger (kept apart from the
/// audited definition). A row belongs to one version and enabled state of the workflow; a change starts over.
/// </summary>
[NotAudited]
public sealed class WorkflowSchedule : ITenantOwned, IVersioned
{
    /// <summary>The workflow's id.</summary>
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The workflow version the row was computed for.</summary>
    public int WorkflowVersion { get; set; }

    /// <summary>The next occurrence of a schedule.</summary>
    public DateTimeOffset? NextAt { get; set; }

    /// <summary>Up to when date triggers were checked.</summary>
    public DateTimeOffset? CheckedUntil { get; set; }

    public uint Version { get; set; }
}

/// <summary>Kinds of bookmarks the engine creates itself; other modules complete bookmarks of their own kinds.</summary>
public static class BookmarkKinds
{
    public const string Approval = "approval";
    public const string Delay = "delay";

    /// <summary>A failed node waits before it runs again (its retry policy).</summary>
    public const string Retry = "retry";
}

/// <summary>
/// A durable wait of a run (ADR-0036): an approval, a delay, a retry, or anything another module or workflow completes
/// (e.g. the questions an AI batch answers: waits of kind <c>ai.batch</c> with the question as <see cref="Data"/>). It is
/// completed with a payload, together with the message that resumes the run; bookmarks with a <see cref="ResumeAt"/> are
/// completed by the minute job when their time has come.
/// </summary>
[NotAudited]
public sealed class WorkflowBookmark : ITenantOwned, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RunId { get; set; }

    /// <summary>The node that waits.</summary>
    public required string Node { get; set; }

    public required string Kind { get; set; }

    /// <summary>What completes it, unique per kind (e.g. the approval id).</summary>
    public required string Key { get; set; }

    public DateTimeOffset? ResumeAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>What completed it as a JSON object; becomes the node's output (its <c>outcome</c> picks the port).</summary>
    public string? Payload { get; set; }

    /// <summary>What the wait is about as a JSON object (e.g. the question a batch answers), from the activity.</summary>
    public string? Data { get; set; }

    /// <summary>When completed, the node runs again instead of taking the payload as its output (<c>WaitAndRunAgain</c>).</summary>
    public bool RunAgain { get; set; }

    public uint Version { get; set; }
}

public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3,
}

/// <summary>An approval step waiting for a decision of one of its assignees.</summary>
public sealed class ApprovalRequest : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RunId { get; set; }

    public required string StepName { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public required string Title { get; set; }

    public string? ReviewType { get; set; }

    public string? ReviewKey { get; set; }

    public List<Guid> Assignees { get; set; } = [];

    /// <summary>Added as assignees (and notified) when the request is overdue.</summary>
    public List<Guid> EscalateTo { get; set; } = [];

    public DateTimeOffset? DueAt { get; set; }

    public bool Escalated { get; set; }

    public ApprovalStatus Status { get; set; }

    public Guid? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

public sealed class WorkflowsDbContext(DbContextOptions<WorkflowsDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    /// <summary>The storage name from before the rename to workflows (ADR-0036); migration history is kept per schema.</summary>
    public const string Schema = "automation";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<WorkflowDefinition> Workflows => Set<WorkflowDefinition>();

    public DbSet<WorkflowVersion> Versions => Set<WorkflowVersion>();

    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();

    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();

    public DbSet<WorkflowBookmark> Bookmarks => Set<WorkflowBookmark>();

    public DbSet<WorkflowSchedule> Schedules => Set<WorkflowSchedule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<WorkflowDefinition>(b =>
        {
            b.ToTable("definitions");
            b.Property(a => a.Name).HasMaxLength(200);
            b.Property(a => a.Description).HasMaxLength(2000);
            b.Property(a => a.Trigger).HasMaxLength(200);
            b.HasIndex(a => new { a.TenantId, a.WorkspaceId, a.Name }).IsUnique();
            b.HasIndex(a => new { a.TenantId, a.WorkspaceId, a.Trigger });
            b.Property(a => a.BuiltInKey).HasMaxLength(200);
            b.Property(a => a.CopiedFrom).HasMaxLength(200);
            b.Property(a => a.Key).HasMaxLength(200);
            b.HasIndex(a => new { a.TenantId, a.WorkspaceId, a.BuiltInKey, a.ListId }).IsUnique();
            b.HasIndex(a => new { a.TenantId, a.WorkspaceId, a.Key });
        });
        modelBuilder.Entity<WorkflowVersion>(b =>
        {
            b.ToTable("versions");
            b.HasIndex(v => new { v.WorkflowId, v.Number }).IsUnique();
        });
        modelBuilder.Entity<WorkflowRun>(b =>
        {
            b.Property(x => x.Concurrency).HasMaxLength(20);
            b.ToTable("runs");
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.Error).HasMaxLength(2000);
            b.Property(r => r.Node).HasMaxLength(200);
            b.Property(r => r.FailedNode).HasMaxLength(200);
            b.HasIndex(r => new { r.WorkflowId, r.EventId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.ItemId });
            b.HasIndex(r => new { r.TenantId, r.WorkflowId, r.StartedAt });
            b.HasIndex(r => new { r.Status, r.LastActivityAt });
            b.HasIndex(r => new { r.TenantId, r.Status, r.CompletedAt });
        });
        modelBuilder.Entity<ApprovalRequest>(b =>
        {
            b.ToTable("approvals");
            b.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(a => a.StepName).HasMaxLength(200);
            b.Property(a => a.Title).HasMaxLength(1000);
            b.Property(a => a.Comment).HasMaxLength(2000);
            b.HasIndex(a => new { a.TenantId, a.Status, a.DueAt });
            b.HasIndex(a => a.RunId);
        });
        modelBuilder.Entity<WorkflowBookmark>(b =>
        {
            b.ToTable("bookmarks");
            b.Property(k => k.Node).HasMaxLength(200);
            b.Property(k => k.Kind).HasMaxLength(100);
            b.Property(k => k.Key).HasMaxLength(200);
            b.HasIndex(k => new { k.TenantId, k.Kind, k.Key }).IsUnique();
            b.HasIndex(k => k.RunId);
            b.HasIndex(k => new { k.CompletedAt, k.ResumeAt });
        });
        modelBuilder.Entity<WorkflowSchedule>(b =>
        {
            b.ToTable("schedules");
            b.Property(s => s.Id).ValueGeneratedNever();
        });
        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
