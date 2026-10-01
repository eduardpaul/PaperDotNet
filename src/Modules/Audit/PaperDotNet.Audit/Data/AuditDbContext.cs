using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Audit.Data;

/// <summary>The audit log. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class AuditDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public AuditDbContext(DbContextOptions<AuditDbContext> options)
        : base(options)
    {
    }

    public DbSet<AuditEntry> AuditEntries { get; set; } = null!;

    /// <summary>Changes of every module (table <c>audit_log</c>, written by <c>IAuditLogWriter</c>).</summary>
    public DbSet<AuditLogEntry> AuditLog { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.ToTable("audit_entries");
            entry.Property(e => e.Action).HasMaxLength(64);
            entry.HasIndex(e => new { e.TenantId, e.Id });
            entry.HasIndex(e => new { e.TenantId, e.TargetId });
        });
        modelBuilder.Entity<AuditLogEntry>(entry =>
        {
            entry.ToTable("audit_log");
            entry.Property(e => e.Action).HasMaxLength(16);
            entry.Property(e => e.EntityType).HasMaxLength(128);
            entry.Property(e => e.TraceId).HasMaxLength(32);
            entry.HasIndex(e => new { e.TenantId, e.Id });
            entry.HasIndex(e => new { e.TenantId, e.EntityId });
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class AuditDesignTimeFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<AuditDbContext>());
}
