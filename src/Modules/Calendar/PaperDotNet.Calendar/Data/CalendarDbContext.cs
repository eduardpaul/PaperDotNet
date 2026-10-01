using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Calendar.Data;

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>A repeating event (CAL-02): the rule is expanded in <see cref="TimeZone"/>, so 9:00 stays 9:00 across DST.</summary>
public class EventRecurrence : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>An RFC 5545 RRULE, e.g. <c>FREQ=WEEKLY;BYDAY=MO,WE</c>.</summary>
    public string Rule { get; set; } = "";

    /// <summary>IANA time zone of the series (e.g. <c>Europe/Berlin</c>).</summary>
    public string TimeZone { get; set; } = "UTC";

    [ConcurrencyCheck]
    public uint Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// An exception to a series: a cancelled occurrence (<see cref="OverrideItemId"/> null) or one moved or changed, which
/// is then its own event item.
/// </summary>
public class OccurrenceChange : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public Guid MasterItemId { get; set; }

    /// <summary>
    /// The start the occurrence would have had (RECURRENCE-ID), in Unix milliseconds: SQLite cannot compare
    /// <see cref="DateTimeOffset"/> values in SQL (ADR-0039).
    /// </summary>
    public long OriginalStartUnixMs { get; set; }

    public Guid? OverrideItemId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset OriginalStart => DateTimeOffset.FromUnixTimeMilliseconds(OriginalStartUnixMs);
}

/// <summary>The iCalendar UID of an imported event, so importing again updates instead of duplicating.</summary>
public class EventSource : ITenantOwned, INotAudited
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public string Uid { get; set; } = "";

    public Guid ItemId { get; set; }
}

/// <summary>A read-only calendar subscription (CAL-04): an unguessable URL that acts as its owner.</summary>
public class CalendarFeed : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>One list, or null for every calendar and task list the owner can read.</summary>
    public Guid? WorkspaceId { get; set; }

    public Guid? ListId { get; set; }

    /// <summary>SHA-256 of the secret part of the token (the token is shown once).</summary>
    public string SecretHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

#pragma warning restore CA1852

/// <summary>Series, exceptions, import sources and feeds. Query rules as in every module (ADR-0039).</summary>
public class CalendarDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public CalendarDbContext(DbContextOptions<CalendarDbContext> options)
        : base(options)
    {
    }

    public DbSet<EventRecurrence> Recurrences { get; set; } = null!;

    public DbSet<OccurrenceChange> OccurrenceChanges { get; set; } = null!;

    public DbSet<EventSource> Sources { get; set; } = null!;

    public DbSet<CalendarFeed> Feeds { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecurrence>(b =>
        {
            b.ToTable("event_recurrences");
            b.Property(r => r.Rule).HasMaxLength(500);
            b.Property(r => r.TimeZone).HasMaxLength(64);
            b.HasIndex(r => new { r.TenantId, r.ItemId }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.ListId });
        });
        modelBuilder.Entity<OccurrenceChange>(b =>
        {
            b.ToTable("event_occurrence_changes");
            b.Ignore(e => e.OriginalStart);
            b.HasIndex(e => new { e.TenantId, e.MasterItemId, e.OriginalStartUnixMs }).IsUnique();
            b.HasIndex(e => new { e.TenantId, e.ListId });
            b.HasIndex(e => new { e.TenantId, e.OverrideItemId });
        });
        modelBuilder.Entity<EventSource>(b =>
        {
            b.ToTable("event_sources");
            b.Property(s => s.Uid).HasMaxLength(255);
            b.HasIndex(s => new { s.TenantId, s.ListId, s.Uid }).IsUnique();
            b.HasIndex(s => new { s.TenantId, s.ItemId });
        });
        modelBuilder.Entity<CalendarFeed>(b =>
        {
            b.ToTable("calendar_feeds");
            b.Property(f => f.Name).HasMaxLength(200);
            b.Property(f => f.SecretHash).HasMaxLength(64);
            b.HasIndex(f => new { f.TenantId, f.SecretHash }).IsUnique();
            b.HasIndex(f => new { f.TenantId, f.UserId });
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class CalendarDesignTimeFactory : IDesignTimeDbContextFactory<CalendarDbContext>
{
    public CalendarDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<CalendarDbContext>());
}
