using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.Documents.Data;

/// <summary>What happens when an upload has the same content as an existing document (DOC-10).</summary>
public enum DuplicatePolicy
{
    /// <summary>Accept silently.</summary>
    Allow = 0,

    /// <summary>Accept and list the existing documents in the response (default).</summary>
    Warn = 1,

    /// <summary>Reject with 409 <c>duplicateFile</c>.</summary>
    Block = 2,
}

/// <summary>
/// A stored file, content-addressed by SHA-256 per tenant (DOC-11): identical content is stored once.
/// Rows without versions are removed by <c>StoredFileCleanupJob</c> after a grace period.
/// </summary>
[NotAudited]
public sealed class StoredFile : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Lower-case hex SHA-256 of the content.</summary>
    public required string Sha256 { get; set; }

    public long Size { get; set; }

    public required string MediaType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last time an upload used this content (protects it from cleanup while in use).</summary>
    public DateTimeOffset LastUsedAt { get; set; }

    public string BlobKey => $"{TenantId:N}/{Sha256[..2]}/{Sha256}";
}

/// <summary>A version of a library item's file (DOC-03). Versions are never changed; the original is always kept.</summary>
public sealed class FileVersion : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>1, 2, … per item.</summary>
    public int Number { get; set; }

    /// <summary>The item's current file.</summary>
    public bool IsCurrent { get; set; }

    public Guid StoredFileId { get; set; }

    public required string Sha256 { get; set; }

    public long Size { get; set; }

    public required string MediaType { get; set; }

    public required string FileName { get; set; }

    /// <summary>What created the version: <c>upload</c>, <c>restore</c> (later: <c>ocr</c>, <c>pages</c>).</summary>
    public required string Source { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>Document settings of a library.</summary>
public sealed class LibrarySettings : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public DuplicatePolicy DuplicatePolicy { get; set; } = DuplicatePolicy.Warn;

    public uint Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "documents";

    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    public DbSet<FileVersion> FileVersions => Set<FileVersion>();

    public DbSet<LibrarySettings> LibrarySettings => Set<LibrarySettings>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFile>(b =>
        {
            b.ToTable("stored_files");
            b.Property(f => f.Sha256).HasMaxLength(64);
            b.Property(f => f.MediaType).HasMaxLength(100);
            b.HasIndex(f => new { f.TenantId, f.Sha256 }).IsUnique();
        });
        modelBuilder.Entity<FileVersion>(b =>
        {
            b.ToTable("file_versions");
            b.Property(v => v.Sha256).HasMaxLength(64);
            b.Property(v => v.MediaType).HasMaxLength(100);
            b.Property(v => v.FileName).HasMaxLength(255);
            b.Property(v => v.Source).HasMaxLength(20);
            b.HasIndex(v => new { v.ItemId, v.Number }).IsUnique();
            b.HasIndex(v => new { v.TenantId, v.Sha256, v.IsCurrent });
            b.HasIndex(v => v.StoredFileId);
        });
        modelBuilder.Entity<LibrarySettings>(b =>
        {
            b.ToTable("library_settings");
            b.Property(s => s.DuplicatePolicy).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(s => new { s.TenantId, s.ListId }).IsUnique();
        });
    }
}
