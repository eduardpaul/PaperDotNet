using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Tasks.Data;

/// <summary>How two items are related (TSK-02, TSK-06); no enums are stored under AOT (ADR-0039).</summary>
public static class TaskLinkKinds
{
    /// <summary>The target task is a subtask of the item (a task has at most one parent).</summary>
    public const string Subtask = "subtask";

    /// <summary>The item is blocked by the target task.</summary>
    public const string BlockedBy = "blockedBy";

    /// <summary>The task is about the target document.</summary>
    public const string Document = "document";

    public static bool IsValid(string? kind) => kind is Subtask or BlockedBy or Document;
}

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>A relation from a task (the item) to another item (the target).</summary>
public class TaskLink : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>A <see cref="TaskLinkKinds"/> value.</summary>
    public string Kind { get; set; } = "";

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
public class ChecklistEntry : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public int Position { get; set; }

    public string Text { get; set; } = "";

    public bool Done { get; set; }
}

/// <summary>A repeating task (TSK-05): completing it creates the next occurrence, which takes the rule over.</summary>
public class TaskRecurrence : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>An RFC 5545 RRULE, e.g. <c>FREQ=WEEKLY;BYDAY=MO</c>.</summary>
    public string Rule { get; set; } = "";

    [ConcurrencyCheck]
    public uint Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

#pragma warning restore CA1852

/// <summary>Checklists, links and recurrences of tasks. Query rules as in every module (ADR-0039).</summary>
public class TasksDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public TasksDbContext(DbContextOptions<TasksDbContext> options)
        : base(options)
    {
    }

    public DbSet<TaskLink> Links { get; set; } = null!;

    public DbSet<ChecklistEntry> Checklist { get; set; } = null!;

    public DbSet<TaskRecurrence> Recurrences { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskLink>(b =>
        {
            b.ToTable("task_links");
            b.Property(l => l.Kind).HasMaxLength(20);
            b.HasIndex(l => new { l.TenantId, l.ItemId, l.TargetItemId, l.Kind }).IsUnique();
            b.HasIndex(l => new { l.TenantId, l.TargetItemId, l.Kind });
        });
        modelBuilder.Entity<ChecklistEntry>(b =>
        {
            b.ToTable("task_checklists");
            b.Property(c => c.Text).HasMaxLength(500);
            b.HasIndex(c => new { c.TenantId, c.ItemId, c.Position });
        });
        modelBuilder.Entity<TaskRecurrence>(b =>
        {
            b.ToTable("task_recurrences");
            b.Property(r => r.Rule).HasMaxLength(500);
            b.HasIndex(r => new { r.TenantId, r.ItemId }).IsUnique();
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class TasksDesignTimeFactory : IDesignTimeDbContextFactory<TasksDbContext>
{
    public TasksDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<TasksDbContext>());
}
