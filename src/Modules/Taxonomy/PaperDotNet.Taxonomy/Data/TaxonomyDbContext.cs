using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Taxonomy.Data;

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>A group of term sets (SharePoint term group), e.g. "Finance".</summary>
public class TermGroup : ITenantOwned, IAuditable, IVersioned
{
    public const string SystemName = "System";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Created by the system (holds the keywords set); cannot be renamed.</summary>
    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>A vocabulary of hierarchical terms. Open sets accept new terms from users.</summary>
public class TermSet : ITenantOwned, IAuditable, IVersioned
{
    public const string KeywordsName = "Keywords";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid GroupId { get; set; }

    public string Name { get; set; } = "";

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

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>A label of a term in another language.</summary>
public sealed record TermLabel(string Language, string Name);

public class Term : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TermSetId { get; set; }

    public Guid? ParentId { get; set; }

    /// <summary>Default label.</summary>
    public string Name { get; set; } = "";

    /// <summary>Lower-cased default label, unique among siblings.</summary>
    public string NormalizedName { get; set; } = "";

    /// <summary>Materialized path of ids (<c>/root/child/</c>, including itself) for subtree queries.</summary>
    public string Path { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Display color, e.g. <c>#1f77b4</c> (Papermerge-style colored tags).</summary>
    public string? Color { get; set; }

    /// <summary>Labels in other languages as a JSON array (<see cref="GetLabels"/>).</summary>
    public string Labels { get; set; } = "[]";

    /// <summary>Synonyms as a JSON array (<see cref="GetSynonyms"/>).</summary>
    public string Synonyms { get; set; } = "[]";

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

    [ConcurrencyCheck]
    public uint Version { get; set; }

    public List<TermLabel> GetLabels() => JsonSerializer.Deserialize(Labels, TaxonomyDataJson.Default.ListTermLabel) ?? [];

    public void SetLabels(IEnumerable<TermLabel> labels) => Labels = JsonSerializer.Serialize([.. labels], TaxonomyDataJson.Default.ListTermLabel);

    public List<string> GetSynonyms() => JsonSerializer.Deserialize(Synonyms, TaxonomyDataJson.Default.ListString) ?? [];

    public void SetSynonyms(IEnumerable<string> synonyms) => Synonyms = JsonSerializer.Serialize([.. synonyms], TaxonomyDataJson.Default.ListString);

    public void RefreshSearchText() =>
        SearchText = string.Join('\n', new[] { Name }.Concat(GetLabels().Select(l => l.Name)).Concat(GetSynonyms())).ToLowerInvariant();
}

#pragma warning restore CA1852

[System.Text.Json.Serialization.JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<TermLabel>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<string>))]
internal sealed partial class TaxonomyDataJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>The term store. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class TaxonomyDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public TaxonomyDbContext(DbContextOptions<TaxonomyDbContext> options)
        : base(options)
    {
    }

    public DbSet<TermGroup> Groups { get; set; } = null!;

    public DbSet<TermSet> TermSets { get; set; } = null!;

    public DbSet<Term> Terms { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TermGroup>(b =>
        {
            b.ToTable("term_groups");
            b.Property(g => g.Name).HasMaxLength(200);
            b.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
        });
        modelBuilder.Entity<TermSet>(b =>
        {
            b.ToTable("term_sets");
            b.Property(s => s.Name).HasMaxLength(200);
            b.Property(s => s.Key).HasMaxLength(150);
            b.Property(s => s.ExtensionId).HasMaxLength(100);
            b.HasIndex(s => new { s.TenantId, s.GroupId, s.Name }).IsUnique();
            b.HasIndex(s => new { s.TenantId, s.Key }).IsUnique();
        });
        modelBuilder.Entity<Term>(b =>
        {
            b.ToTable("terms");
            b.Property(t => t.Name).HasMaxLength(255);
            b.Property(t => t.NormalizedName).HasMaxLength(255);
            b.Property(t => t.Path).HasMaxLength(4000);
            b.Property(t => t.Color).HasMaxLength(32);
            b.HasIndex(t => new { t.TenantId, t.TermSetId, t.ParentId, t.NormalizedName });
            b.HasIndex(t => new { t.TenantId, t.Path });
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class TaxonomyDesignTimeFactory : IDesignTimeDbContextFactory<TaxonomyDbContext>
{
    public TaxonomyDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<TaxonomyDbContext>());
}
