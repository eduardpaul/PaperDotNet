using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Persistence.Sqlite;
using Wolverine.EntityFrameworkCore;

namespace PaperDotNet.Workflows.Data;

/// <summary>Workflows, their versions and runs. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class WorkflowsDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public WorkflowsDbContext(DbContextOptions<WorkflowsDbContext> options)
        : base(options)
    {
    }

    public DbSet<WorkflowDefinition> Workflows { get; set; } = null!;

    public DbSet<WorkflowVersion> WorkflowVersions { get; set; } = null!;

    public DbSet<WorkflowRun> WorkflowRuns { get; set; } = null!;

    public DbSet<WorkflowBookmark> Bookmarks { get; set; } = null!;

    public DbSet<ApprovalRequest> Approvals { get; set; } = null!;

    public DbSet<WorkflowSchedule> WorkflowSchedules { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Runs are started through the outbox (ResumeRun messages) in the same transaction.
        modelBuilder.MapWolverineEnvelopeStorage();

        modelBuilder.Entity<WorkflowDefinition>(workflow =>
        {
            workflow.ToTable("workflows");
            workflow.Property(w => w.Name).HasMaxLength(200);
            workflow.Property(w => w.Key).HasMaxLength(100);
            workflow.Property(w => w.BuiltInKey).HasMaxLength(200);
            workflow.Property(w => w.CopiedFrom).HasMaxLength(200);
            workflow.HasIndex(w => new { w.TenantId, w.WorkspaceId, w.BuiltInKey, w.ListId });
            workflow.Ignore(w => w.EventKey);
            workflow.HasIndex(w => new { w.TenantId, w.WorkspaceId, w.Key });
            workflow.HasIndex(w => new { w.TenantId, w.WorkspaceId, w.Name }).IsUnique();
            workflow.HasIndex(w => new { w.TenantId, w.WorkspaceId, w.Enabled });
        });

        modelBuilder.Entity<WorkflowVersion>(version =>
        {
            version.ToTable("workflow_versions");
            version.HasIndex(v => new { v.TenantId, v.WorkflowId, v.Number }).IsUnique();
            version.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(v => v.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowRun>(run =>
        {
            run.ToTable("workflow_runs");
            run.Property(r => r.Status).HasMaxLength(16);
            run.HasIndex(r => new { r.TenantId, r.WorkflowId, r.Id });
            run.HasIndex(r => new { r.TenantId, r.WorkspaceId, r.Id });
            run.HasIndex(r => new { r.TenantId, r.ItemId, r.Id });
            run.HasIndex(r => new { r.TenantId, r.WorkflowId, r.ItemId, r.Status });
            run.HasIndex(r => new { r.TenantId, r.Status, r.CompletedAtUnixMs });
            run.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(r => r.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowBookmark>(bookmark =>
        {
            bookmark.ToTable("workflow_bookmarks");
            bookmark.Property(b => b.Kind).HasMaxLength(100);
            bookmark.Property(b => b.Key).HasMaxLength(200);
            bookmark.Property(b => b.Node).HasMaxLength(200);
            bookmark.HasIndex(b => new { b.TenantId, b.Kind, b.Key }).IsUnique();
            bookmark.HasIndex(b => new { b.TenantId, b.RunId });
            bookmark.HasIndex(b => new { b.TenantId, b.ResumeAtUnixMs });
        });

        modelBuilder.Entity<WorkflowSchedule>(schedule =>
        {
            schedule.ToTable("workflow_schedules");
            schedule.HasIndex(s => s.TenantId);
        });

        modelBuilder.Entity<ApprovalRequest>(approval =>
        {
            approval.ToTable("workflow_approvals");
            approval.Property(a => a.Title).HasMaxLength(1000);
            approval.Property(a => a.Status).HasMaxLength(16);
            approval.Property(a => a.Node).HasMaxLength(200);
            approval.Property(a => a.Comment).HasMaxLength(2000);
            approval.HasIndex(a => new { a.TenantId, a.RunId });
            approval.HasIndex(a => new { a.TenantId, a.Status, a.DueAtUnixMs });
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class WorkflowsDesignTimeFactory : IDesignTimeDbContextFactory<WorkflowsDbContext>
{
    public WorkflowsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<WorkflowsDbContext>());
}
