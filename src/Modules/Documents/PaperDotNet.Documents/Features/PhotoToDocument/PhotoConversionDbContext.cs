using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.Documents.Features.PhotoToDocument;

public sealed class PhotoConversionRecord : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RunId { get; set; }
    public Guid PrimaryItemId { get; set; }
    public Guid StartedBy { get; set; }
    public string State { get; set; } = "preparing";
    public string FileName { get; set; } = "document.pdf";
    public string Languages { get; set; } = "eng";
    public string SourcesJson { get; set; } = "[]";
}

public sealed class PhotoConversionDbContext(DbContextOptions<PhotoConversionDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    // Retain the original schema and migration history so pending reviews survive the move into Documents.
    public const string Schema = "ext_paperdotnet_storageoptimization";
    public DbSet<PhotoConversionRecord> Conversions => Set<PhotoConversionRecord>();
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PhotoConversionRecord>(b =>
        {
            b.ToTable("photo_conversions");
            b.HasIndex(c => c.RunId);
            b.Property(c => c.State).HasMaxLength(20);
            b.Property(c => c.FileName).HasMaxLength(255);
        });
    }
}

