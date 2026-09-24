using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.Calendar.Data;

/// <summary>A repeating event (CAL-02): the rule is expanded in <see cref="TimeZone"/>, so 9:00 stays 9:00 across DST.</summary>
public sealed class EventRecurrence : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>An RFC 5545 RRULE, e.g. <c>FREQ=WEEKLY;BYDAY=MO,WE</c>.</summary>
    public required string Rule { get; set; }

    /// <summary>IANA time zone of the series (e.g. <c>Europe/Berlin</c>).</summary>
    public required string TimeZone { get; set; }

    public uint Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// An exception to a series: a cancelled occurrence (<see cref="OverrideItemId"/> null) or one moved or
/// changed, which is then its own event item.
/// </summary>
public sealed class OccurrenceChange : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid MasterItemId { get; set; }

    /// <summary>The start the occurrence would have had (RECURRENCE-ID), UTC.</summary>
    public DateTimeOffset OriginalStart { get; set; }

    public Guid? OverrideItemId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>The iCalendar UID of an imported event, so importing again updates instead of duplicating.</summary>
[NotAudited]
public sealed class EventSource : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ListId { get; set; }

    public required string Uid { get; set; }

    public Guid ItemId { get; set; }
}

/// <summary>A read-only calendar subscription (CAL-04): an unguessable URL that acts as its owner.</summary>
public sealed class CalendarFeed : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    /// <summary>One list, or null for every calendar and task list the owner can read.</summary>
    public Guid? WorkspaceId { get; set; }

    public Guid? ListId { get; set; }

    /// <summary>SHA-256 of the secret part of the token (the token is shown once).</summary>
    public required string SecretHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

public sealed class CalendarDbContext(DbContextOptions<CalendarDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "calendar";

    public DbSet<EventRecurrence> Recurrences => Set<EventRecurrence>();

    public DbSet<OccurrenceChange> OccurrenceChanges => Set<OccurrenceChange>();

    public DbSet<EventSource> Sources => Set<EventSource>();

    public DbSet<CalendarFeed> Feeds => Set<CalendarFeed>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecurrence>(b =>
        {
            b.ToTable("recurrences");
            b.Property(r => r.Rule).HasMaxLength(500);
            b.Property(r => r.TimeZone).HasMaxLength(64);
            b.HasIndex(r => r.ItemId).IsUnique();
            b.HasIndex(r => r.ListId);
        });
        modelBuilder.Entity<OccurrenceChange>(b =>
        {
            b.ToTable("occurrence_changes");
            b.HasIndex(e => new { e.MasterItemId, e.OriginalStart }).IsUnique();
            b.HasIndex(e => e.OverrideItemId);
        });
        modelBuilder.Entity<EventSource>(b =>
        {
            b.ToTable("sources");
            b.Property(s => s.Uid).HasMaxLength(255);
            b.HasIndex(s => new { s.ListId, s.Uid }).IsUnique();
            b.HasIndex(s => s.ItemId);
        });
        modelBuilder.Entity<CalendarFeed>(b =>
        {
            b.ToTable("feeds");
            b.Property(f => f.Name).HasMaxLength(200);
            b.Property(f => f.SecretHash).HasMaxLength(64);
            b.HasIndex(f => f.SecretHash).IsUnique();
            b.HasIndex(f => f.UserId);
        });
    }
}
