using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.Collaboration.Data;

/// <summary>A comment on a list item (LST-17); replies point to a top-level comment.</summary>
public sealed class Comment : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>The comment this one replies to (always a top-level comment).</summary>
    public Guid? ParentId { get; set; }

    public required string Text { get; set; }

    /// <summary>Users mentioned in the text (they are notified).</summary>
    public List<Guid> Mentions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

/// <summary>An entry of an item's activity timeline (LST-17).</summary>
[NotAudited]
public sealed class ActivityEntry : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public required string Kind { get; set; }

    public Guid? ActorId { get; set; }

    public string? Summary { get; set; }

    public List<string> ChangedFields { get; set; } = [];

    /// <summary>Makes recording idempotent (e.g. the event id).</summary>
    public string? DeduplicationKey { get; set; }

    public DateTimeOffset At { get; set; }
}

public sealed class CollaborationDbContext(DbContextOptions<CollaborationDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "collaboration";

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(b =>
        {
            b.ToTable("comments");
            b.Property(c => c.Text).HasMaxLength(CommentRules.MaxLength);
            b.HasIndex(c => new { c.TenantId, c.ItemId });
            b.HasIndex(c => c.ParentId);
        });
        modelBuilder.Entity<ActivityEntry>(b =>
        {
            b.ToTable("activity");
            b.Property(a => a.Kind).HasMaxLength(100);
            b.Property(a => a.Summary).HasMaxLength(1000);
            b.Property(a => a.DeduplicationKey).HasMaxLength(200);
            b.HasIndex(a => new { a.TenantId, a.ItemId, a.At });
            b.HasIndex(a => new { a.TenantId, a.DeduplicationKey }).IsUnique();
        });
    }
}

internal static class CommentRules
{
    public const int MaxLength = 4000;
    public const int MaxMentions = 20;
}
