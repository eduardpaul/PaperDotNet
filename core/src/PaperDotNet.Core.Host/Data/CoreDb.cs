using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace PaperDotNet.Core.Host.Data;

/// <summary>
/// The core's one DbContext (ADR-0039). At run time it uses the compiled model and the precompiled queries generated at
/// publish, so the model below is only built at design time and in tests.
/// <para>
/// Query rules that keep queries precompilable: one LINQ expression from a DbSet property to the terminal operator,
/// no query composed across statements, and the DbContext and every value the query captures copied into locals first
/// (EF Core cannot yet bind method or lambda parameters, dotnet/efcore#35887). Tenant-owned sets always filter on
/// <c>TenantId</c>: there are no global query filters under AOT. Dynamic queries (list item filters) go through
/// <see cref="Persistence.Sqlite.SqliteItemQueries"/>.
/// </para>
/// </summary>
public class CoreDb : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public CoreDb(DbContextOptions<CoreDb> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants { get; set; } = null!;

    public DbSet<User> Users { get; set; } = null!;

    public DbSet<ListDefinition> Lists { get; set; } = null!;

    public DbSet<ListItem> Items { get; set; } = null!;

    public DbSet<AuditEntry> AuditEntries { get; set; } = null!;

    public DbSet<WorkflowDefinition> Workflows { get; set; } = null!;

    public DbSet<WorkflowVersion> WorkflowVersions { get; set; } = null!;

    public DbSet<WorkflowRun> WorkflowRuns { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Wolverine's inbox/outbox tables, written in the same transaction; Wolverine creates them itself.
        modelBuilder.MapWolverineEnvelopeStorage();

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants");
            tenant.Property(t => t.Identifier).HasMaxLength(64);
            tenant.Property(t => t.Name).HasMaxLength(200);
            tenant.HasIndex(t => t.Identifier).IsUnique();
        });

        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.UserName).HasMaxLength(256);
            user.Property(u => u.NormalizedUserName).HasMaxLength(256);
            user.Property(u => u.DisplayName).HasMaxLength(256);
            user.HasIndex(u => new { u.TenantId, u.NormalizedUserName }).IsUnique();
            user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ListDefinition>(list =>
        {
            list.ToTable("lists");
            list.Property(l => l.Name).HasMaxLength(255);
            list.HasIndex(l => new { l.TenantId, l.Name }).IsUnique();
            list.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ListItem>(item =>
        {
            item.ToTable("list_items");
            item.Property(i => i.Title).HasMaxLength(255);
            item.HasIndex(i => new { i.TenantId, i.ListId, i.Id });
            item.HasIndex(i => new { i.TenantId, i.ListId, i.Title });
            item.HasOne<ListDefinition>().WithMany().HasForeignKey(i => i.ListId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.ToTable("audit_entries");
            entry.Property(e => e.Action).HasMaxLength(64);
            entry.HasIndex(e => new { e.TenantId, e.Id });
            entry.HasIndex(e => new { e.TenantId, e.TargetId });
        });

        modelBuilder.Entity<WorkflowDefinition>(workflow =>
        {
            workflow.ToTable("workflows");
            workflow.Property(w => w.Name).HasMaxLength(200);
            workflow.HasIndex(w => new { w.TenantId, w.Name }).IsUnique();
            workflow.HasIndex(w => new { w.TenantId, w.Enabled });
            workflow.HasOne<Tenant>().WithMany().HasForeignKey(w => w.TenantId).OnDelete(DeleteBehavior.Cascade);
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
            run.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(r => r.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
