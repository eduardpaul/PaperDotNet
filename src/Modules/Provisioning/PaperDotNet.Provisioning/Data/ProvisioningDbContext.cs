using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Provisioning.Data;

/// <summary>Values of <see cref="PortabilityPackage.Kind"/> (strings, not an enum: ADR-0039).</summary>
public static class PackageKinds
{
    public const string Export = "export";
    public const string Import = "import";
}

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>
/// A package being exported or imported (PLT-13): the file in blob storage (set once an export is written), who asked
/// for it and until when it is kept.
/// </summary>
public class PortabilityPackage : ITenantOwned, INotAudited
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Kind { get; set; } = PackageKinds.Export;

    /// <summary>The exported workspace, or the workspace an import is applied to; null for the whole tenant.</summary>
    public Guid? WorkspaceId { get; set; }

    public Guid OperationId { get; set; }

    /// <summary>The package in blob storage; null until an export is written.</summary>
    public string? BlobKey { get; set; }

    public long Size { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// <see cref="CreatedAt"/> and <see cref="ExpiresAt"/> in Unix milliseconds, for ordering and comparing in SQL
    /// (SQLite cannot compare <see cref="DateTimeOffset"/> values, ADR-0039).
    /// </summary>
    public long CreatedAtUnixMs { get; set; }

    public long ExpiresAtUnixMs { get; set; }
}

#pragma warning restore CA1852

/// <summary>Export and import packages. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class ProvisioningDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public ProvisioningDbContext(DbContextOptions<ProvisioningDbContext> options)
        : base(options)
    {
    }

    public DbSet<PortabilityPackage> Packages { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<PortabilityPackage>(b =>
        {
            b.ToTable("portability_packages");
            b.Property(p => p.Kind).HasMaxLength(20);
            b.Property(p => p.BlobKey).HasMaxLength(300);
            b.HasIndex(p => new { p.TenantId, p.CreatedBy, p.CreatedAtUnixMs });
            b.HasIndex(p => new { p.TenantId, p.ExpiresAtUnixMs });
        });
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class ProvisioningDesignTimeFactory : IDesignTimeDbContextFactory<ProvisioningDbContext>
{
    public ProvisioningDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<ProvisioningDbContext>());
}
