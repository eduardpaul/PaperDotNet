using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.Tasks.Data;

/// <summary>How two items are related (TSK-02, TSK-06).</summary>
public enum TaskLinkKind
{
    /// <summary>The target task is a subtask of the item (a task has at most one parent).</summary>
    Subtask = 0,

    /// <summary>The item is blocked by the target task.</summary>
    BlockedBy = 1,

    /// <summary>The task is about the target document.</summary>
    Document = 2,
}

/// <summary>A relation from a task (the item) to another item (the target).</summary>
public sealed class TaskLink : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public TaskLinkKind Kind { get; set; }

    public Guid ItemId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid TargetItemId { get; set; }

    public Guid TargetWorkspaceId { get; set; }

    public Guid TargetListId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>One entry of a task's checklist (TSK-01).</summary>
public sealed class ChecklistEntry : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public int Position { get; set; }

    public required string Text { get; set; }

    public bool Done { get; set; }
}

/// <summary>A repeating task (TSK-05): completing it creates the next occurrence, which takes the rule over.</summary>
public sealed class TaskRecurrence : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>An RFC 5545 RRULE, e.g. <c>FREQ=WEEKLY;BYDAY=MO</c>.</summary>
    public required string Rule { get; set; }

    public uint Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

public sealed class TasksDbContext(DbContextOptions<TasksDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "tasks";

    public DbSet<TaskLink> Links => Set<TaskLink>();

    public DbSet<ChecklistEntry> Checklist => Set<ChecklistEntry>();

    public DbSet<TaskRecurrence> Recurrences => Set<TaskRecurrence>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskLink>(b =>
        {
            b.ToTable("links");
            b.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(l => new { l.ItemId, l.TargetItemId, l.Kind }).IsUnique();
            b.HasIndex(l => new { l.TargetItemId, l.Kind });
        });
        modelBuilder.Entity<ChecklistEntry>(b =>
        {
            b.ToTable("checklist");
            b.Property(c => c.Text).HasMaxLength(500);
            b.HasIndex(c => new { c.ItemId, c.Position });
        });
        modelBuilder.Entity<TaskRecurrence>(b =>
        {
            b.ToTable("recurrences");
            b.Property(r => r.Rule).HasMaxLength(500);
            b.HasIndex(r => r.ItemId).IsUnique();
        });
    }
}
