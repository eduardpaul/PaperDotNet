using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.Notes.Data;

/// <summary>A note and its title, so wiki links find notes by title (case-insensitive) within a workspace.</summary>
public sealed class NoteEntry : ITenantOwned
{
    public Guid ItemId { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public required string Title { get; set; }

    /// <summary><see cref="Features.NoteMarkdown.Normalize"/> of the title.</summary>
    public required string NormalizedTitle { get; set; }
}

/// <summary>A <c>[[wiki link]]</c> in a note's body, resolved to the note it points to when one has that title.</summary>
public sealed class NoteLink : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid SourceItemId { get; set; }

    public Guid SourceListId { get; set; }

    /// <summary>Position in the body (0 first).</summary>
    public int Ordinal { get; set; }

    /// <summary>The link target as written, e.g. <c>Meeting notes</c> in <c>[[Meeting notes#Actions|see]]</c>.</summary>
    public required string Target { get; set; }

    public required string NormalizedTarget { get; set; }

    public string? Heading { get; set; }

    public string? Alias { get; set; }

    /// <summary><c>![[…]]</c>: the target is embedded, not just linked.</summary>
    public bool Embed { get; set; }

    /// <summary>The note with that title, or null while there is none.</summary>
    public Guid? TargetItemId { get; set; }
}

public sealed class NotesDbContext(DbContextOptions<NotesDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "notes";

    public DbSet<NoteEntry> Notes => Set<NoteEntry>();

    public DbSet<NoteLink> Links => Set<NoteLink>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
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
            b.ToTable("links");
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
