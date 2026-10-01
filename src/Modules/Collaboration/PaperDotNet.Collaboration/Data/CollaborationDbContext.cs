using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Collaboration.Data;

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>A comment on a list item (LST-17); replies point to a top-level comment.</summary>
public class Comment : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>The comment this one replies to (always a top-level comment).</summary>
    public Guid? ParentId { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Users mentioned in the text (they are notified), as a JSON array of ids.</summary>
    public string Mentions { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>An entry of an item's activity timeline (LST-17).</summary>
public class ActivityEntry : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public string Kind { get; set; } = "";

    public Guid? ActorId { get; set; }

    public string? Summary { get; set; }

    /// <summary>The changed fields as a JSON array of names.</summary>
    public string ChangedFields { get; set; } = "[]";

    /// <summary>Makes recording idempotent (e.g. the event id).</summary>
    public string? DeduplicationKey { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>
    /// <see cref="At"/> in Unix milliseconds: the timeline's order. SQLite cannot order or compare
    /// <see cref="DateTimeOffset"/> values in SQL (ADR-0039).
    /// </summary>
    public long AtUnixMs { get; set; }
}

#pragma warning restore CA1852

/// <summary>Comments and item activity. Query rules as in every module (ADR-0039): locals, one expression, explicit <c>TenantId</c>.</summary>
public class CollaborationDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public CollaborationDbContext(DbContextOptions<CollaborationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Comment> Comments { get; set; } = null!;

    public DbSet<ActivityEntry> Activity { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(b =>
        {
            b.ToTable("comments");
            b.Property(c => c.Text).HasMaxLength(CommentRules.MaxLength);
            b.HasIndex(c => new { c.TenantId, c.ItemId });
            b.HasIndex(c => new { c.TenantId, c.ParentId });
        });
        modelBuilder.Entity<ActivityEntry>(b =>
        {
            b.ToTable("item_activity");
            b.Property(a => a.Kind).HasMaxLength(100);
            b.Property(a => a.Summary).HasMaxLength(1000);
            b.Property(a => a.DeduplicationKey).HasMaxLength(200);
            b.HasIndex(a => new { a.TenantId, a.ItemId, a.AtUnixMs });
            b.HasIndex(a => new { a.TenantId, a.DeduplicationKey }).IsUnique();
        });
    }
}

internal static class CommentRules
{
    public const int MaxLength = 4000;
    public const int MaxMentions = 20;
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class CollaborationDesignTimeFactory : IDesignTimeDbContextFactory<CollaborationDbContext>
{
    public CollaborationDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<CollaborationDbContext>());
}
