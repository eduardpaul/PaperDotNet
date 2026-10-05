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

/// <summary>A version of a library item's file (DOC-03). Versions are immutable; approved storage optimization explicitly releases the reviewed source version.</summary>
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

    /// <summary>What created the version: <c>upload</c>, <c>restore</c>, <c>ocr</c>, <c>pages</c>, <c>import</c>.</summary>
    public required string Source { get; set; }

    public int? PageCount { get; set; }

    /// <summary>Language of the text (Tesseract code, e.g. <c>eng</c>), from OCR or the library settings.</summary>
    public string? TextLanguage { get; set; }

    /// <summary>
    /// Languages chosen for this file (DOC-17), e.g. <c>fra+eng</c>; null uses the library's languages, then the
    /// uploader's document languages. New versions of the item keep them.
    /// </summary>
    public string? Languages { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>Text of one page of a stored file (from its text layer or OCR), for search.</summary>
[NotAudited]
public sealed class StoredFilePage : ITenantOwned
{
    public Guid StoredFileId { get; set; }

    public int PageNumber { get; set; }

    public Guid TenantId { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>Document settings of a library.</summary>
public sealed class LibrarySettings : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public DuplicatePolicy DuplicatePolicy { get; set; } = DuplicatePolicy.Warn;

    /// <summary>
    /// The default languages of OCR (<c>document.ocr</c>, ADR-0038): Tesseract languages, e.g. <c>eng</c> or <c>deu+eng</c> (the first one is used for stemming); null uses the
    /// organization's default document languages (PLT-18).
    /// </summary>
    public string? OcrLanguages { get; set; }

    public uint Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>The library that is a group's inbox (DOC-16): members upload into it and find it in their inboxes.</summary>
public sealed class GroupInbox : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid GroupId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>A staged replacement: orchestration stays in workflows; this row owns the temporary file references.</summary>
public sealed class FileCandidate : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RunId { get; set; }
    public Guid ItemId { get; set; }
    public Guid SourceVersionId { get; set; }
    public Guid SourceStoredFileId { get; set; }
    public Guid StoredFileId { get; set; }
    public Guid? PromotedVersionId { get; set; }
    public string SourceJson { get; set; } = "{}";
    public string MetricsJson { get; set; } = "{}";
    public string State { get; set; } = "pending";
    public string FileName { get; set; } = "document.webp";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Temporary document bytes owned by a processing extension or another service.</summary>
public sealed class StagedFile : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Owner { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public Guid? StoredFileId { get; set; }
    public bool HasPreparedText { get; set; }
    public int? PageCount { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public string FileName { get; set; } = "document";
    public string? Languages { get; set; }
    public string State { get; set; } = "preparing";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Content references protected while a temporary document is retained.</summary>
public sealed class StagedFileReference : ITenantOwned
{
    public Guid StagedFileId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoredFileId { get; set; }
}

public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "documents";

    public DbSet<FileCandidate> Candidates => Set<FileCandidate>();
    public DbSet<StagedFile> StagedFiles => Set<StagedFile>();
    public DbSet<StagedFileReference> StagedReferences => Set<StagedFileReference>();

    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    public DbSet<FileVersion> FileVersions => Set<FileVersion>();

    public DbSet<LibrarySettings> LibrarySettings => Set<LibrarySettings>();

    public DbSet<GroupInbox> GroupInboxes => Set<GroupInbox>();

    public DbSet<StoredFilePage> Pages => Set<StoredFilePage>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StagedFile>(b =>
        {
            b.ToTable("staged_files");
            b.Property(c => c.Owner).HasMaxLength(100);
            b.Property(c => c.State).HasMaxLength(20);
            b.Property(c => c.FileName).HasMaxLength(255);
        });
        modelBuilder.Entity<StagedFileReference>(b =>
        {
            b.ToTable("staged_file_references");
            b.HasKey(c => new { c.StagedFileId, c.StoredFileId });
            b.HasOne<StagedFile>().WithMany().HasForeignKey(c => c.StagedFileId);
        });
        modelBuilder.Entity<FileCandidate>(b =>
        {
            b.ToTable("file_candidates");
            b.HasIndex(c => new { c.TenantId, c.SourceVersionId });
            b.HasIndex(c => c.RunId);
            b.Property(c => c.State).HasMaxLength(20);
            b.Property(c => c.FileName).HasMaxLength(255);
        });
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
            b.Property(v => v.TextLanguage).HasMaxLength(20);
            b.Property(v => v.Languages).HasMaxLength(100);
            b.HasIndex(v => new { v.ItemId, v.Number }).IsUnique();
            b.HasIndex(v => new { v.TenantId, v.ItemId }).IsUnique().HasFilter("\"is_current\" = TRUE");
            b.HasIndex(v => new { v.TenantId, v.Sha256, v.IsCurrent });
            b.HasIndex(v => v.StoredFileId);
        });
        modelBuilder.Entity<StoredFilePage>(b =>
        {
            b.ToTable("stored_file_pages");
            b.HasKey(p => new { p.StoredFileId, p.PageNumber });
        });
        modelBuilder.Entity<LibrarySettings>(b =>
        {
            b.ToTable("library_settings");
            b.Property(s => s.DuplicatePolicy).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.OcrLanguages).HasMaxLength(100);
            b.HasIndex(s => new { s.TenantId, s.ListId }).IsUnique();
        });
        modelBuilder.Entity<GroupInbox>(b =>
        {
            b.ToTable("group_inboxes");
            b.HasIndex(g => new { g.TenantId, g.GroupId }).IsUnique();
        });
    }
}
