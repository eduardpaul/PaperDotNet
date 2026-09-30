using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Workspaces.Data;

/// <summary>Workspaces and their members. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class WorkspacesDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public WorkspacesDbContext(DbContextOptions<WorkspacesDbContext> options)
        : base(options)
    {
    }

    public DbSet<Workspace> Workspaces { get; set; } = null!;

    public DbSet<WorkspaceMember> Members { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>(workspace =>
        {
            workspace.ToTable("workspaces");
            workspace.Property(w => w.Name).HasMaxLength(200);
            workspace.Property(w => w.Description).HasMaxLength(2000);
            workspace.HasIndex(w => new { w.TenantId, w.PersonalOwnerId }).IsUnique();
        });
        modelBuilder.Entity<WorkspaceMember>(member =>
        {
            member.ToTable("workspace_members");
            member.HasKey(m => new { m.WorkspaceId, m.UserId });
            member.Property(m => m.Role).HasMaxLength(16);
            member.HasIndex(m => new { m.TenantId, m.UserId });
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class WorkspacesDesignTimeFactory : IDesignTimeDbContextFactory<WorkspacesDbContext>
{
    public WorkspacesDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<WorkspacesDbContext>());
}
