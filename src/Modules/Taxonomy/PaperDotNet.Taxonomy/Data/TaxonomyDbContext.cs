using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Taxonomy.Data;

/// <summary>A group of term sets (SharePoint term group), e.g. "Finance".</summary>
public sealed class TermGroup : ITenantOwned, IAuditable, IVersioned
{
    public const string SystemName = "System";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Created by the system (holds the keywords set); cannot be renamed.</summary>
    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

/// <summary>A vocabulary of hierarchical terms. Open sets accept new terms from users.</summary>
public sealed class TermSet : ITenantOwned, IAuditable, IVersioned
{
    public const string KeywordsName = "Keywords";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid GroupId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Users may add terms (e.g. by typing a new value in a field).</summary>
    public bool IsOpen { get; set; }

    /// <summary>The tenant's folksonomy set (exactly one per tenant).</summary>
    public bool IsKeywords { get; set; }

    /// <summary>Stable key of a term set provisioned from a template (extensions, TAX-11).</summary>
    public string? Key { get; set; }

    /// <summary>The extension that provides this term set, if any.</summary>
    public string? ExtensionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

/// <summary>A label of a term in another language.</summary>
public sealed class TermLabel
{
    public string Language { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class Term : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TermSetId { get; set; }

    public Guid? ParentId { get; set; }

    /// <summary>Default label.</summary>
    public required string Name { get; set; }

    /// <summary>Lower-cased default label, unique among siblings.</summary>
    public required string NormalizedName { get; set; }

    /// <summary>Materialized path of ids (<c>/root/child/</c>, including itself) for subtree queries.</summary>
    public required string Path { get; set; }

    public string? Description { get; set; }

    /// <summary>Display color, e.g. <c>#1f77b4</c> (Papermerge-style colored tags).</summary>
    public string? Color { get; set; }

    public List<TermLabel> Labels { get; set; } = [];

    public List<string> Synonyms { get; set; } = [];

    /// <summary>Lower-cased name, labels and synonyms, for search and autocomplete.</summary>
    public string SearchText { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>Deprecated terms stay on existing items but cannot be assigned again.</summary>
    public bool IsDeprecated { get; set; }

    /// <summary>Set when the term was merged into another term.</summary>
    public Guid? MergedIntoId { get; set; }

    /// <summary>The term can be used in keywords fields (a keyword promoted into this term set, TAX-05).</summary>
    public bool AvailableAsKeyword { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }

    public void RefreshSearchText() =>
        SearchText = string.Join('\n', new[] { Name }.Concat(Labels.Select(l => l.Name)).Concat(Synonyms)).ToLowerInvariant();
}

public sealed class TaxonomyDbContext(DbContextOptions<TaxonomyDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "taxonomy";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<TermGroup> Groups => Set<TermGroup>();

    public DbSet<TermSet> TermSets => Set<TermSet>();

    public DbSet<Term> Terms => Set<Term>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<TermGroup>(b =>
        {
            b.ToTable("groups");
            b.Property(g => g.Name).HasMaxLength(200);
            b.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
        });
        modelBuilder.Entity<TermSet>(b =>
        {
            b.ToTable("term_sets");
            b.Property(s => s.Name).HasMaxLength(200);
            b.HasIndex(s => new { s.GroupId, s.Name }).IsUnique();
            b.Property(s => s.Key).HasMaxLength(150);
            b.Property(s => s.ExtensionId).HasMaxLength(100);
            b.HasIndex(s => new { s.TenantId, s.Key }).IsUnique();
        });
        modelBuilder.Entity<Term>(b =>
        {
            b.ToTable("terms");
            b.Property(t => t.Name).HasMaxLength(255);
            b.Property(t => t.NormalizedName).HasMaxLength(255);
            b.Property(t => t.Path).HasMaxLength(4000);
            b.Property(t => t.Color).HasMaxLength(32);
            b.HasIndex(t => new { t.TermSetId, t.ParentId, t.NormalizedName });
            b.HasIndex(t => t.Path);
            b.ComplexCollection(t => t.Labels, l => l.ToJson());
        });
        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
