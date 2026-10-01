using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Notes.Data;

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>A note and its title, so wiki links find notes by title (case-insensitive) within a workspace.</summary>
public class NoteEntry : ITenantOwned
{
    public Guid ItemId { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public string Title { get; set; } = "";

    /// <summary><see cref="Features.NoteMarkdown.Normalize"/> of the title.</summary>
    public string NormalizedTitle { get; set; } = "";
}

/// <summary>A <c>[[wiki link]]</c> in a note's body, resolved to the note it points to when one has that title.</summary>
public class NoteLink : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid SourceItemId { get; set; }

    public Guid SourceListId { get; set; }

    /// <summary>Position in the body (0 first).</summary>
    public int Ordinal { get; set; }

    /// <summary>The link target as written, e.g. <c>Meeting notes</c> in <c>[[Meeting notes#Actions|see]]</c>.</summary>
    public string Target { get; set; } = "";

    public string NormalizedTarget { get; set; } = "";

    public string? Heading { get; set; }

    public string? Alias { get; set; }

    /// <summary><c>![[…]]</c>: the target is embedded, not just linked.</summary>
    public bool Embed { get; set; }

    /// <summary>The note with that title, or null while there is none.</summary>
    public Guid? TargetItemId { get; set; }
}

#pragma warning restore CA1852

/// <summary>Note titles and wiki links. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class NotesDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public NotesDbContext(DbContextOptions<NotesDbContext> options)
        : base(options)
    {
    }

    public DbSet<NoteEntry> Notes { get; set; } = null!;

    public DbSet<NoteLink> Links { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NoteEntry>(b =>
        {
            b.ToTable("notes");
            b.HasKey(n => n.ItemId);
            b.Property(n => n.Title).HasMaxLength(1024);
            b.Property(n => n.NormalizedTitle).HasMaxLength(1024);
            b.HasIndex(n => new { n.TenantId, n.WorkspaceId, n.NormalizedTitle });
        });
        modelBuilder.Entity<NoteLink>(b =>
        {
            b.ToTable("note_links");
            b.Property(l => l.Target).HasMaxLength(1024);
            b.Property(l => l.NormalizedTarget).HasMaxLength(1024);
            b.Property(l => l.Heading).HasMaxLength(1024);
            b.Property(l => l.Alias).HasMaxLength(1024);
            b.HasIndex(l => new { l.TenantId, l.SourceItemId });
            b.HasIndex(l => new { l.TenantId, l.TargetItemId });
            b.HasIndex(l => new { l.TenantId, l.WorkspaceId, l.NormalizedTarget });
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class NotesDesignTimeFactory : IDesignTimeDbContextFactory<NotesDbContext>
{
    public NotesDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<NotesDbContext>());
}
