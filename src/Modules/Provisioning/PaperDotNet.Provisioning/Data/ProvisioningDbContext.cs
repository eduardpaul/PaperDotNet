using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.Provisioning.Data;

public enum PackageKind
{
    Export = 0,
    Import = 1,
}

/// <summary>
/// A package being exported or imported (PLT-13): the file in blob storage (set once an export is written), who asked
/// for it and until when it is kept.
/// </summary>
public sealed class PortabilityPackage : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public PackageKind Kind { get; set; }

    /// <summary>The exported workspace, or the workspace an import is applied to; null for the whole tenant.</summary>
    public Guid? WorkspaceId { get; set; }

    public Guid OperationId { get; set; }

    /// <summary>The package in blob storage; null until an export is written.</summary>
    public string? BlobKey { get; set; }

    public long Size { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ProvisioningDbContext(DbContextOptions<ProvisioningDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "provisioning";

    public DbSet<PortabilityPackage> Packages => Set<PortabilityPackage>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<PortabilityPackage>(b =>
        {
            b.ToTable("packages");
            b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(p => p.BlobKey).HasMaxLength(300);
            b.HasIndex(p => new { p.TenantId, p.CreatedBy, p.CreatedAt });
            b.HasIndex(p => p.ExpiresAt);
        });
}
