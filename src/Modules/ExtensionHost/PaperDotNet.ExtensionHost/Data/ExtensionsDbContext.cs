using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.ExtensionHost.Data;

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>An extension's state in one tenant (EXT-03). No row: the manifest's <c>autoEnable</c> applies.</summary>
public class TenantExtension : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string ExtensionId { get; set; } = "";

    public bool Enabled { get; set; }

    /// <summary>Settings values as a JSON object (validated against the manifest).</summary>
    public string Settings { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

#pragma warning restore CA1852

/// <summary>Per-tenant state of extensions. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class ExtensionsDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public ExtensionsDbContext(DbContextOptions<ExtensionsDbContext> options)
        : base(options)
    {
    }

    public DbSet<TenantExtension> TenantExtensions { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantExtension>(extension =>
        {
            extension.ToTable("tenant_extensions");
            extension.Property(e => e.ExtensionId).HasMaxLength(100);
            extension.HasIndex(e => new { e.TenantId, e.ExtensionId }).IsUnique();
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class ExtensionsDesignTimeFactory : IDesignTimeDbContextFactory<ExtensionsDbContext>
{
    public ExtensionsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<ExtensionsDbContext>());
}
