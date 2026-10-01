using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Documents.Data;

/// <summary>What happens when an upload has the same content as an existing document (DOC-10); stored as text (ADR-0039).</summary>
public static class DuplicatePolicies
{
    /// <summary>Accept silently.</summary>
    public const string Allow = "allow";

    /// <summary>Accept and list the existing documents in the response (default).</summary>
    public const string Warn = "warn";

    /// <summary>Reject with 409 <c>duplicateFile</c>.</summary>
    public const string Block = "block";

    public static bool IsValid(string value) => value is Allow or Warn or Block;
}

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>
/// A stored file, content-addressed by SHA-256 per tenant (DOC-11): identical content is stored once. Rows without
/// versions are removed by <c>StoredFileCleanupJob</c> after a grace period.
/// </summary>
public class StoredFile : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Lower-case hex SHA-256 of the content.</summary>
    public string Sha256 { get; set; } = "";

    public long Size { get; set; }

    public string MediaType { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last time an upload used this content in Unix milliseconds (protects it from cleanup while in use).</summary>
    public long LastUsedAtUnixMs { get; set; }

    public string BlobKey => $"{TenantId:N}/{Sha256[..2]}/{Sha256}";
}

/// <summary>A version of a library item's file (DOC-03). Versions are never changed; the original is always kept.</summary>
public class FileVersion : ITenantOwned, IAuditable
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

    public string Sha256 { get; set; } = "";

    public long Size { get; set; }

    public string MediaType { get; set; } = "";

    public string FileName { get; set; } = "";

    /// <summary>What created the version: <c>upload</c>, <c>restore</c>, <c>ocr</c>, <c>pages</c>, <c>import</c>.</summary>
    public string Source { get; set; } = "";

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
public class StoredFilePage : ITenantOwned
{
    public Guid StoredFileId { get; set; }

    public int PageNumber { get; set; }

    public Guid TenantId { get; set; }

    public string Text { get; set; } = "";
}

/// <summary>Document settings of a library.</summary>
public class LibrarySettings : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>One of <see cref="DuplicatePolicies"/>.</summary>
    public string DuplicatePolicy { get; set; } = DuplicatePolicies.Warn;

    /// <summary>
    /// The default languages of OCR (<c>document.ocr</c>, ADR-0038): Tesseract languages, e.g. <c>eng</c> or <c>deu+eng</c>
    /// (the first one is used for stemming); null uses the organization's default document languages (PLT-18).
    /// </summary>
    public string? OcrLanguages { get; set; }

    public uint Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>The library that is a group's inbox (DOC-16): members upload into it and find it in their inboxes.</summary>
public class GroupInbox : ITenantOwned, IAuditable
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

#pragma warning restore CA1852

/// <summary>Files of libraries. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class DocumentsDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public DocumentsDbContext(DbContextOptions<DocumentsDbContext> options)
        : base(options)
    {
    }

    public DbSet<StoredFile> StoredFiles { get; set; } = null!;

    public DbSet<FileVersion> FileVersions { get; set; } = null!;

    public DbSet<LibrarySettings> LibrarySettings { get; set; } = null!;

    public DbSet<GroupInbox> GroupInboxes { get; set; } = null!;

    public DbSet<StoredFilePage> Pages { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFile>(b =>
        {
            b.ToTable("stored_files");
            b.Property(f => f.Sha256).HasMaxLength(64);
            b.Property(f => f.MediaType).HasMaxLength(100);
            b.HasIndex(f => new { f.TenantId, f.Sha256 }).IsUnique();
            b.HasIndex(f => new { f.TenantId, f.LastUsedAtUnixMs });
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
            b.HasIndex(v => new { v.TenantId, v.ItemId, v.IsCurrent });
            b.HasIndex(v => new { v.TenantId, v.Sha256, v.IsCurrent });
            b.HasIndex(v => new { v.TenantId, v.StoredFileId });
        });
        modelBuilder.Entity<StoredFilePage>(b =>
        {
            b.ToTable("stored_file_pages");
            b.HasKey(p => new { p.StoredFileId, p.PageNumber });
            b.HasIndex(p => new { p.TenantId, p.StoredFileId });
        });
        modelBuilder.Entity<LibrarySettings>(b =>
        {
            b.ToTable("library_settings");
            b.Property(s => s.DuplicatePolicy).HasMaxLength(20);
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

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class DocumentsDesignTimeFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<DocumentsDbContext>());
}
