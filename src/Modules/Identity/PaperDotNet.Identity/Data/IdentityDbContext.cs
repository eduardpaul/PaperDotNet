using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Identity.Data;

/// <summary>Tenants and users. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class IdentityDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants { get; set; } = null!;

    public DbSet<User> Users { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<IdentityDbContext>());
}
