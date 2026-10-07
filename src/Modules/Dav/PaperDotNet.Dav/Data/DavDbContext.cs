using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.Dav.Data;

/// <summary>
/// A WebDAV lock (RFC 4918, class 2), held for desktop apps while they edit a file. Locks are on paths as the client
/// sees them; expired ones are ignored and removed by <c>dav.cleanup</c>.
/// </summary>
[NotAudited]
public sealed class DavLock : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The lock token, e.g. <c>urn:uuid:…</c>.</summary>
    public required string StateToken { get; set; }

    /// <summary>The locked path relative to <c>/dav/</c>.</summary>
    public required string Path { get; set; }

    public required string Href { get; set; }

    public bool Recursive { get; set; }

    public required string AccessType { get; set; }

    public required string ShareMode { get; set; }

    /// <summary>The requested timeout in seconds; -1 for infinite.</summary>
    public long TimeoutSeconds { get; set; }

    /// <summary>The client's <c>owner</c> XML.</summary>
    public string? Owner { get; set; }

    public string? OwnerHref { get; set; }

    /// <summary>Who holds the lock (Home paths are per user).</summary>
    public Guid UserId { get; set; }

    public DateTimeOffset Issued { get; set; }

    public DateTimeOffset? LastRefresh { get; set; }

    public DateTimeOffset Expiration { get; set; }
}

/// <summary>
/// A temporary file of a desktop app (<c>~$Report.docx</c>, <c>~WRD0001.tmp</c>, …): kept for its user only, never a
/// document, removed after a day. <see cref="ItemId"/> marks a document the app renamed to a temporary name while
/// saving (Office's safe save), so that moving the new content onto the original name replaces its file.
/// </summary>
[NotAudited]
public sealed class DavTransientFile : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>The folder, or the library itself (its list id) at the root.</summary>
    public Guid ParentId { get; set; }

    public required string Name { get; set; }

    /// <summary><see cref="Name"/> in upper case, for case-insensitive lookups.</summary>
    public required string NameKey { get; set; }

    /// <summary>The content in the blob store; null for a renamed document (<see cref="ItemId"/>).</summary>
    public string? BlobKey { get; set; }

    public long Size { get; set; }

    /// <summary>A document shown under this temporary name.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>The document's name before it was renamed (<see cref="ItemId"/>).</summary>
    public string? OriginalName { get; set; }

    /// <summary>The document's file version when it was renamed: the content this entry shows.</summary>
    public Guid? VersionId { get; set; }

    /// <summary>
    /// The renamed document got new content under its original name (Office saved), so it is shown there again; this
    /// entry keeps showing the old content until the app deletes it.
    /// </summary>
    public bool Released { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Tables of the WebDAV module (schema <c>dav</c>, ADR-0047).</summary>
public sealed class DavDbContext(DbContextOptions<DavDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "dav";

    public DbSet<DavLock> Locks => Set<DavLock>();

    public DbSet<DavTransientFile> TransientFiles => Set<DavTransientFile>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DavLock>(b =>
        {
            b.ToTable("locks");
            b.Property(l => l.StateToken).HasMaxLength(100);
            b.Property(l => l.Path).HasMaxLength(4000);
            b.Property(l => l.Href).HasMaxLength(4000);
            b.Property(l => l.AccessType).HasMaxLength(20);
            b.Property(l => l.ShareMode).HasMaxLength(20);
            b.Property(l => l.Owner).HasMaxLength(4000);
            b.Property(l => l.OwnerHref).HasMaxLength(4000);
            b.HasIndex(l => l.StateToken).IsUnique();
            b.HasIndex(l => l.Expiration);
        });
        modelBuilder.Entity<DavTransientFile>(b =>
        {
            b.ToTable("transient_files");
            b.Property(f => f.Name).HasMaxLength(255);
            b.Property(f => f.NameKey).HasMaxLength(255);
            b.Property(f => f.BlobKey).HasMaxLength(200);
            b.Property(f => f.OriginalName).HasMaxLength(255);
            b.HasIndex(f => new { f.UserId, f.ParentId, f.NameKey }).IsUnique();
            b.HasIndex(f => f.ExpiresAt);
        });
    }
}
