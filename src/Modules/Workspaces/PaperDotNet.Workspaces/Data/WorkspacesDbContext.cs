using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
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

    /// <summary>Saves; a change of memberships or workspaces changes what users may access (<see cref="AccessGeneration"/>).</summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var tenants = ChangeTracker.Entries()
            .Where(e => e.Entity is WorkspaceMember or Workspace && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => ((ITenantOwned)e.Entity).TenantId)
            .ToHashSet();
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        foreach (var tenant in tenants)
        {
            AccessGeneration.Next(tenant);
        }

        return saved;
    }

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
