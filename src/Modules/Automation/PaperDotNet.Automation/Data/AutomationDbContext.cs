using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Automation.Data;

/// <summary>
/// An automation of a workspace (EVT-07, EVT-08): a trigger, an optional condition and steps. The definition
/// lives in immutable versions; runs keep the version they started with.
/// </summary>
public sealed class AutomationDefinition : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Trigger type of the current version, kept as a column to find automations for an event quickly.</summary>
    public required string Trigger { get; set; }

    /// <summary>The version new runs use.</summary>
    public int CurrentVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

/// <summary>An immutable version of an automation's definition.</summary>
[NotAudited]
public sealed class AutomationVersion : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AutomationId { get; set; }

    public int Number { get; set; }

    /// <summary>The <c>AutomationSpec</c> as JSON (names, never ids, so it is portable).</summary>
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
/// A run of an automation: the interpreter's position, step outcomes and a short log. A run started by an event
/// records the event (unique per automation, so a redelivered event starts nothing).
/// </summary>
[NotAudited]
public sealed class AutomationRun : ITenantOwned, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AutomationId { get; set; }

    public int AutomationVersion { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The item the run works on; null for extension triggers without an item.</summary>
    public Guid? ListId { get; set; }

    public Guid? ItemId { get; set; }

    /// <summary>The event that started the run; null for manual starts.</summary>
    public Guid? EventId { get; set; }

    /// <summary>Data of an extension trigger as a JSON object, if any.</summary>
    public string? Data { get; set; }

    public RunStatus Status { get; set; }

    /// <summary>Index of the next instruction.</summary>
    public int Position { get; set; }

    /// <summary>Outcomes of approval steps by step name, as a JSON object.</summary>
    public string Outcomes { get; set; } = "{}";

    /// <summary>Recent log entries as a JSON array.</summary>
    public string Log { get; set; } = "[]";

    public string? Error { get; set; }

    /// <summary>The wait the run is in (an approval or a delay), if any; resume messages must name it.</summary>
    public string? WaitingFor { get; set; }

    /// <summary>When a delay is over (the minute job resumes the run then).</summary>
    public DateTimeOffset? ResumeAt { get; set; }

    public int Depth { get; set; }

    /// <summary>Id of the current action step's execution (stable across retries of the step).</summary>
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

public sealed class AutomationDbContext(DbContextOptions<AutomationDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "automation";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<AutomationDefinition> Automations => Set<AutomationDefinition>();

    public DbSet<AutomationVersion> Versions => Set<AutomationVersion>();

    public DbSet<AutomationRun> Runs => Set<AutomationRun>();

    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<AutomationDefinition>(b =>
        {
            b.ToTable("definitions");
            b.Property(a => a.Name).HasMaxLength(200);
            b.Property(a => a.Description).HasMaxLength(2000);
            b.Property(a => a.Trigger).HasMaxLength(200);
            b.HasIndex(a => new { a.TenantId, a.WorkspaceId, a.Name }).IsUnique();
            b.HasIndex(a => new { a.TenantId, a.WorkspaceId, a.Trigger });
        });
        modelBuilder.Entity<AutomationVersion>(b =>
        {
            b.ToTable("versions");
            b.HasIndex(v => new { v.AutomationId, v.Number }).IsUnique();
        });
        modelBuilder.Entity<AutomationRun>(b =>
        {
            b.ToTable("runs");
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.Error).HasMaxLength(2000);
            b.Property(r => r.WaitingFor).HasMaxLength(100);
            b.HasIndex(r => new { r.AutomationId, r.EventId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.ItemId });
            b.HasIndex(r => new { r.TenantId, r.AutomationId, r.StartedAt });
            b.HasIndex(r => new { r.Status, r.ResumeAt });
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
        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
