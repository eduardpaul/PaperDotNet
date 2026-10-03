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

    /// <summary>Permission scope (ADR-0035): the document is found by callers who can read this scope.</summary>
    public Guid ScopeId { get; set; }

    public required string Title { get; set; }

    /// <summary>High-weight text (SRC-06).</summary>
    public string Keywords { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>Language of the text for stemming (SRC-05), e.g. <c>english</c>; null = exact words only.</summary>
    public string? Language { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A term (tag) of a document, for facets and hierarchical tag filters (SRC-03).</summary>
[NotAudited]
public sealed class SearchTag : ITenantOwned
{
    public Guid DocumentId { get; set; }

    public Guid TermId { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>
/// One value of a document's search field (ADR-0043): <see cref="Text"/> for keyword, reference and term fields,
/// <see cref="Number"/> for the others (see <c>SearchValue</c>). Multi-value fields have one row per value.
/// </summary>
[NotAudited]
public sealed class SearchFieldValue : ITenantOwned
{
    public Guid DocumentId { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public int Kind { get; set; }

    /// <summary>Position among the field's values (0 first).</summary>
    public int Ordinal { get; set; }

    public string? Text { get; set; }

    public double? Number { get; set; }
}

/// <summary>
/// A passage of a document (SRC-07, SRC-09): a window of its text with the page it is on (null for text that is not
/// on a page, like the title and fields). Passages have their own full-text index, so keyword hits can point to a
/// page, and an embedding for semantic search, computed in the background.
/// </summary>
[NotAudited]
public sealed class SearchPassage : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    /// <summary>Position in the document (0 first).</summary>
    public int Ordinal { get; set; }

    /// <summary>1-based page number, or null.</summary>
    public int? Page { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? Language { get; set; }

    /// <summary>SHA-256 (hex) of the text that is embedded (title and passage): an unchanged passage keeps its embedding.</summary>
    public required string ContentHash { get; set; }

    /// <summary>The normalized embedding as little-endian float32 values, or null until computed.</summary>
    public byte[]? Embedding { get; set; }

    /// <summary>The model <see cref="Embedding"/> comes from; a different configured model means it is embedded again.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>When the embedding was stored (UTC ticks), so vector indexes in memory load only what changed.</summary>
    public long VectorStamp { get; set; }
}

public sealed class SearchDbContext(DbContextOptions<SearchDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "search";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<SearchDocument> Documents => Set<SearchDocument>();

    public DbSet<SearchTag> Tags => Set<SearchTag>();

    public DbSet<SearchPassage> Passages => Set<SearchPassage>();

    public DbSet<SearchFieldValue> FieldValues => Set<SearchFieldValue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<SearchDocument>(b =>
        {
            b.ToTable("documents");
            b.Property(d => d.SourceType).HasMaxLength(50);
            b.Property(d => d.Title).HasMaxLength(1024);
            b.HasIndex(d => new { d.TenantId, d.ContainerId });
            b.HasIndex(d => new { d.ScopeId, d.Id });
            b.Property(d => d.Language).HasMaxLength(20);
            b.HasFullTextIndex(nameof(SearchDocument.Title), nameof(SearchDocument.Keywords), nameof(SearchDocument.Body));
            b.HasFullTextLanguage(nameof(SearchDocument.Language));
        });
        modelBuilder.Entity<SearchTag>(b =>
        {
            b.ToTable("document_tags");
            b.HasKey(t => new { t.DocumentId, t.TermId });
            b.HasIndex(t => new { t.TermId, t.DocumentId });
        });
        modelBuilder.Entity<SearchFieldValue>(b =>
        {
            b.ToTable("document_fields");
            b.HasKey(v => new { v.DocumentId, v.Name, v.Kind, v.Ordinal });
            b.Property(v => v.Name).HasMaxLength(128);
            b.Property(v => v.Text).HasMaxLength(256);
            b.HasIndex(v => new { v.TenantId, v.Name, v.Kind, v.Text });
            b.HasIndex(v => new { v.TenantId, v.Name, v.Kind, v.Number });
        });
        modelBuilder.Entity<SearchPassage>(b =>
        {
            b.ToTable("passages");
            b.Property(p => p.Language).HasMaxLength(20);
            b.Property(p => p.ContentHash).HasMaxLength(64);
            b.Property(p => p.EmbeddingModel).HasMaxLength(200);
            b.HasIndex(p => new { p.TenantId, p.DocumentId });
            b.HasIndex(p => new { p.TenantId, p.EmbeddingModel, p.VectorStamp });
            b.HasFullTextIndex(nameof(SearchPassage.Text));
            b.HasFullTextLanguage(nameof(SearchPassage.Language));
        });
        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
