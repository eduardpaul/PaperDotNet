using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Automation.Data;

/// <summary>A rule of a workspace (EVT-07): a trigger, an optional condition and actions (definition as JSON).</summary>
public sealed class AutomationRule : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Trigger type, kept as a column to find rules for an event quickly.</summary>
    public required string Trigger { get; set; }

    /// <summary>The <c>RuleDefinition</c> as JSON (names, never ids, so it is portable).</summary>
    public required string Definition { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

public enum RunStatus
{
    Running = 0,

    /// <summary>Waiting for an approval or a timer.</summary>
    Waiting = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,

    /// <summary>Rules only: the trigger matched but the condition did not.</summary>
    Skipped = 5,
}

/// <summary>One execution of a rule for one event (unique per event: redelivered events do nothing).</summary>
[NotAudited]
public sealed class RuleRun : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RuleId { get; set; }

    public Guid EventId { get; set; }

    public Guid? ItemId { get; set; }

    public RunStatus Status { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>A workflow of a workspace (EVT-08); its steps live in immutable versions.</summary>
public sealed class WorkflowDefinition : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>The version new runs use.</summary>
    public int CurrentVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

/// <summary>An immutable version of a workflow's steps; runs keep the version they started with.</summary>
[NotAudited]
public sealed class WorkflowDefinitionVersion : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DefinitionId { get; set; }

    public int Number { get; set; }

    /// <summary>The <c>WorkflowSteps</c> as JSON.</summary>
    public required string Definition { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}

/// <summary>A workflow run on an item: the interpreter's position, step outcomes and a short log.</summary>
[NotAudited]
public sealed class WorkflowRun : ITenantOwned, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DefinitionId { get; set; }

    public int DefinitionVersion { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public RunStatus Status { get; set; }

    /// <summary>Index of the next instruction.</summary>
    public int Position { get; set; }

    /// <summary>Outcomes of approval steps by step name, as a JSON object.</summary>
    public string Outcomes { get; set; } = "{}";

    /// <summary>Recent log entries as a JSON array.</summary>
    public string Log { get; set; } = "[]";

    public string? Error { get; set; }

    /// <summary>The WorkflowCore instance that drives the run.</summary>
    public string? EngineId { get; set; }

    /// <summary>Event key the run waits for (approval), if any.</summary>
    public string? WaitingFor { get; set; }

    public int Depth { get; set; }

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

    public DbSet<AutomationRule> Rules => Set<AutomationRule>();

    public DbSet<RuleRun> RuleRuns => Set<RuleRun>();

    public DbSet<WorkflowDefinition> Workflows => Set<WorkflowDefinition>();

    public DbSet<WorkflowDefinitionVersion> WorkflowVersions => Set<WorkflowDefinitionVersion>();

    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();

    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<AutomationRule>(b =>
        {
            b.ToTable("rules");
            b.Property(r => r.Name).HasMaxLength(200);
            b.Property(r => r.Trigger).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.WorkspaceId, r.Name }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.WorkspaceId, r.Trigger });
        });
        modelBuilder.Entity<RuleRun>(b =>
        {
            b.ToTable("rule_runs");
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.Error).HasMaxLength(2000);
            b.HasIndex(r => new { r.RuleId, r.EventId }).IsUnique();
            b.HasIndex(r => new { r.RuleId, r.StartedAt });
        });
        modelBuilder.Entity<WorkflowDefinition>(b =>
        {
            b.ToTable("workflows");
            b.Property(w => w.Name).HasMaxLength(200);
            b.Property(w => w.Description).HasMaxLength(2000);
            b.HasIndex(w => new { w.TenantId, w.WorkspaceId, w.Name }).IsUnique();
        });
        modelBuilder.Entity<WorkflowDefinitionVersion>(b =>
        {
            b.ToTable("workflow_versions");
            b.HasIndex(v => new { v.DefinitionId, v.Number }).IsUnique();
        });
        modelBuilder.Entity<WorkflowRun>(b =>
        {
            b.ToTable("workflow_runs");
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.Error).HasMaxLength(2000);
            b.Property(r => r.EngineId).HasMaxLength(100);
            b.Property(r => r.WaitingFor).HasMaxLength(100);
            b.HasIndex(r => new { r.TenantId, r.ItemId });
            b.HasIndex(r => new { r.TenantId, r.DefinitionId, r.StartedAt });
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
