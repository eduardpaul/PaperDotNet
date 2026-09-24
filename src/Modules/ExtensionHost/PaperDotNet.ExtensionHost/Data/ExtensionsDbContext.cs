using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.ExtensionHost.Data;

/// <summary>An extension's state in one tenant (EXT-03). No row: the manifest's <c>autoEnable</c> applies.</summary>
public sealed class TenantExtension : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string ExtensionId { get; set; }

    public bool Enabled { get; set; }

    /// <summary>Settings values as a JSON object (validated against the manifest).</summary>
    public string Settings { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

public sealed class ExtensionsDbContext(DbContextOptions<ExtensionsDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "extensions";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<TenantExtension> TenantExtensions => Set<TenantExtension>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<TenantExtension>(b =>
        {
            b.ToTable("tenant_extensions");
            b.Property(e => e.ExtensionId).HasMaxLength(100);
            b.HasIndex(e => new { e.TenantId, e.ExtensionId }).IsUnique();
        });
        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
