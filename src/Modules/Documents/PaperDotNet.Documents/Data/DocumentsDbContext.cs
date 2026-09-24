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

/// <summary>Processing of a file version (DOC-09): text extraction, OCR, thumbnails.</summary>
public enum ProcessingStatus
{
    /// <summary>Not processed (e.g. automatic processing is off).</summary>
    None = 0,
    Scheduled = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
}

/// <summary>When a library runs OCR.</summary>
public enum OcrMode
{
    /// <summary>For images and PDFs without a usable text layer (default).</summary>
    Auto = 0,

    /// <summary>Only on demand (<c>POST …/file/process</c> with <c>forceOcr</c>).</summary>
    Off = 1,
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

    /// <summary>What created the version: <c>upload</c>, <c>restore</c>, <c>ocr</c> (later: <c>pages</c>).</summary>
    public required string Source { get; set; }

    public ProcessingStatus ProcessingStatus { get; set; }

    public string? ProcessingError { get; set; }

    /// <summary>The operation processing this version (<c>/v1.0/operations/{id}</c>).</summary>
    public Guid? OperationId { get; set; }

    public int? PageCount { get; set; }

    /// <summary>Language of the text (Tesseract code, e.g. <c>eng</c>), from OCR or the library settings.</summary>
    public string? TextLanguage { get; set; }

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

    /// <summary>Process new file versions automatically (text, OCR, thumbnails).</summary>
    public bool AutoProcess { get; set; } = true;

    public OcrMode OcrMode { get; set; } = OcrMode.Auto;

    /// <summary>Tesseract languages, e.g. <c>eng</c> or <c>deu+eng</c> (the first one is used for stemming).</summary>
    public string OcrLanguages { get; set; } = DefaultOcrLanguages;

    public const string DefaultOcrLanguages = "eng";

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

    public DbSet<StoredFilePage> Pages => Set<StoredFilePage>();

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
            b.Property(v => v.ProcessingStatus).HasConversion<string>().HasMaxLength(20);
            b.Property(v => v.ProcessingError).HasMaxLength(1000);
            b.Property(v => v.TextLanguage).HasMaxLength(20);
            b.HasIndex(v => new { v.ItemId, v.Number }).IsUnique();
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
            b.Property(s => s.OcrMode).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.OcrLanguages).HasMaxLength(100);
            b.HasIndex(s => new { s.TenantId, s.ListId }).IsUnique();
        });
    }
}
