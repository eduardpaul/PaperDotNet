using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Search.Data;

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>One searchable document of any data type (SRC-01), maintained by the owning module.</summary>
public class SearchDocument : ITenantOwned, INotAudited
{
    /// <summary>
    /// Integer key: the row id of the full-text index. Declared, so it never changes (SQLite may renumber the implicit
    /// row ids of a table without one, e.g. on <c>VACUUM</c>).
    /// </summary>
    public long Key { get; set; }

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>E.g. <c>listItem</c>.</summary>
    public string SourceType { get; set; } = "";

    public Guid WorkspaceId { get; set; }

    /// <summary>The list (or other container) of the document.</summary>
    public Guid? ContainerId { get; set; }

    public Guid? ContentTypeId { get; set; }

    /// <summary>Permission scope (ADR-0035): the document is found by callers who can read this scope.</summary>
    public Guid ScopeId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>High-weight text (SRC-06).</summary>
    public string Keywords { get; set; } = "";

    public string Body { get; set; } = "";

    /// <summary>Language of the text (SRC-05), e.g. <c>english</c>, or null.</summary>
    public string? Language { get; set; }

    public Guid? CreatedBy { get; set; }

    /// <summary>When the content changed, as Unix milliseconds (ordered and compared in SQL).</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>A term (tag) of a document, for facets, hierarchical tag filters (SRC-03) and term usage.</summary>
public class SearchTag : ITenantOwned, INotAudited
{
    public Guid DocumentId { get; set; }

    public Guid TermId { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>
/// A passage of a document (SRC-07, SRC-09): a window of its text with the page it is on (null for text that is not
/// on a page, like the fields). Passages have their own full-text index, so keyword hits can point to a page.
/// </summary>
public class SearchPassage : ITenantOwned, INotAudited
{
    /// <summary>Integer key: the row id of the passages' full-text index (see <see cref="SearchDocument.Key"/>).</summary>
    public long Key { get; set; }

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    /// <summary>Position in the document (0 first).</summary>
    public int Ordinal { get; set; }

    /// <summary>1-based page number, or null.</summary>
    public int? Page { get; set; }

    public string Text { get; set; } = "";

    /// <summary>SHA-256 (hex) of the title and passage text: an unchanged passage keeps its row (and, later, its embedding).</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>The passage's embedding (normalized little-endian float32), with semantic search (ADR-0027).</summary>
    public byte[]? Embedding { get; set; }

    /// <summary>The model the embedding comes from (<c>provider:model:dimensions</c>); another model embeds again.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>When the embedding was stored (Unix ms): servers load only newer embeddings into memory.</summary>
    public long VectorStamp { get; set; }
}

/// <summary>
/// The search index. The full-text tables (FTS5, <c>search_documents_fts</c> and <c>search_passages_fts</c>) are kept
/// by triggers created in the migration; queries are SQL per provider (<c>ISearchQueries</c>).
/// </summary>
public class SearchDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public SearchDbContext(DbContextOptions<SearchDbContext> options)
        : base(options)
    {
    }

    public DbSet<SearchDocument> Documents { get; set; } = null!;

    public DbSet<SearchTag> Tags { get; set; } = null!;

    public DbSet<SearchPassage> Passages { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SearchDocument>(b =>
        {
            b.ToTable("search_documents");
            b.HasKey(d => d.Key);
            b.Property(d => d.Key).ValueGeneratedOnAdd();
            b.HasIndex(d => d.Id).IsUnique();
            b.Property(d => d.SourceType).HasMaxLength(50);
            b.Property(d => d.Title).HasMaxLength(1024);
            b.Property(d => d.Language).HasMaxLength(20);
            b.HasIndex(d => new { d.TenantId, d.ContainerId });
            b.HasIndex(d => new { d.TenantId, d.SourceType });
            b.HasIndex(d => new { d.TenantId, d.ScopeId });
        });
        modelBuilder.Entity<SearchTag>(b =>
        {
            b.ToTable("search_tags");
            b.HasKey(t => new { t.DocumentId, t.TermId });
            b.HasIndex(t => new { t.TenantId, t.TermId });
        });
        modelBuilder.Entity<SearchPassage>(b =>
        {
            b.ToTable("search_passages");
            b.HasKey(p => p.Key);
            b.Property(p => p.Key).ValueGeneratedOnAdd();
            b.HasIndex(p => p.Id).IsUnique();
            b.Property(p => p.ContentHash).HasMaxLength(64);
            b.Property(p => p.EmbeddingModel).HasMaxLength(200);
            b.HasIndex(p => new { p.TenantId, p.DocumentId });
            b.HasIndex(p => new { p.TenantId, p.EmbeddingModel, p.VectorStamp });
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class SearchDesignTimeFactory : IDesignTimeDbContextFactory<SearchDbContext>
{
    public SearchDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<SearchDbContext>());
}
