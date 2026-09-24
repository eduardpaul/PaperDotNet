using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Search.Data;

/// <summary>One searchable document of any data type (SRC-01), maintained by the owning module.</summary>
[NotAudited]
public sealed class SearchDocument : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>E.g. <c>listItem</c>.</summary>
    public required string SourceType { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The list (or other container) of the document.</summary>
    public Guid? ContainerId { get; set; }

    public Guid? ContentTypeId { get; set; }

    public required string Title { get; set; }

    /// <summary>High-weight text (SRC-06).</summary>
    public string Keywords { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A principal allowed to read a document (security trimming, SRC-04).</summary>
[NotAudited]
public sealed class SearchPrincipal : ITenantOwned
{
    public Guid DocumentId { get; set; }

    public required string Principal { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>A term (tag) of a document, for facets and hierarchical tag filters (SRC-03).</summary>
[NotAudited]
public sealed class SearchTag : ITenantOwned
{
    public Guid DocumentId { get; set; }

    public Guid TermId { get; set; }

    public Guid TenantId { get; set; }
}

public sealed class SearchDbContext(DbContextOptions<SearchDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "search";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<SearchDocument> Documents => Set<SearchDocument>();

    public DbSet<SearchPrincipal> Principals => Set<SearchPrincipal>();

    public DbSet<SearchTag> Tags => Set<SearchTag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<SearchDocument>(b =>
        {
            b.ToTable("documents");
            b.Property(d => d.SourceType).HasMaxLength(50);
            b.Property(d => d.Title).HasMaxLength(1024);
            b.HasIndex(d => new { d.TenantId, d.ContainerId });
            b.HasFullTextIndex(nameof(SearchDocument.Title), nameof(SearchDocument.Keywords), nameof(SearchDocument.Body));
        });
        modelBuilder.Entity<SearchPrincipal>(b =>
        {
            b.ToTable("document_principals");
            b.HasKey(p => new { p.DocumentId, p.Principal });
            b.Property(p => p.Principal).HasMaxLength(40);
            b.HasIndex(p => new { p.Principal, p.DocumentId });
        });
        modelBuilder.Entity<SearchTag>(b =>
        {
            b.ToTable("document_tags");
            b.HasKey(t => new { t.DocumentId, t.TermId });
            b.HasIndex(t => new { t.TermId, t.DocumentId });
        });
        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
