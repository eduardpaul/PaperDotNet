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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Runs are started through the outbox (ResumeRun messages) in the same transaction.
        modelBuilder.MapWolverineEnvelopeStorage();

        modelBuilder.Entity<WorkflowDefinition>(workflow =>
        {
            workflow.ToTable("workflows");
            workflow.Property(w => w.Name).HasMaxLength(200);
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
            run.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(r => r.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class WorkflowsDesignTimeFactory : IDesignTimeDbContextFactory<WorkflowsDbContext>
{
    public WorkflowsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<WorkflowsDbContext>());
}
