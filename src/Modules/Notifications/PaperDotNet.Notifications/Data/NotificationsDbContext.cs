using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.Sqlite;

namespace PaperDotNet.Notifications.Data;

#pragma warning disable CA1852 // Entities stay unsealed: EF Core's precompiled queries cannot use sealed entity types (ADR-0039).

/// <summary>A notification in a user's inbox (NTF-01).</summary>
public class Notification : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public string Type { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Body { get; set; }

    public Guid? WorkspaceId { get; set; }

    public Guid? ListId { get; set; }

    public Guid? ItemId { get; set; }

    public string? DeduplicationKey { get; set; }

    /// <summary>Shown in the inbox (false when the user turned the in-app channel off for the type).</summary>
    public bool InInbox { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>A user's notification settings (NTF-04, NTF-05).</summary>
public class NotificationSettings : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Channel choices per type as JSON: <c>{"reminder":{"inApp":true,"webhook":false}}</c>.</summary>
    public string Channels { get; set; } = "{}";

    public string? WebhookUrl { get; set; }

    /// <summary>The webhook signing secret, protected with Data Protection.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Quiet hours (local time in the user's preferred time zone, PLT-17): webhook deliveries wait until they end.</summary>
    public TimeOnly? QuietHoursStart { get; set; }

    public TimeOnly? QuietHoursEnd { get; set; }

    /// <summary>Hour of the day (in the user's preferred time zone) for daily digests.</summary>
    public int DigestHour { get; set; } = 7;

    [ConcurrencyCheck]
    public uint Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>Status of a webhook or change delivery (no enums are stored under AOT, ADR-0039).</summary>
public static class DeliveryStatuses
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
}

/// <summary>A notification to post to a user's webhook; retried with backoff.</summary>
public class WebhookDelivery : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid NotificationId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>A <see cref="DeliveryStatuses"/> value.</summary>
    public string Status { get; set; } = DeliveryStatuses.Pending;

    public int Attempts { get; set; }

    /// <summary>
    /// When to try next. SQLite cannot compare <see cref="DateTimeOffset"/> values in SQL: pending deliveries are
    /// selected by status and their times compared in memory.
    /// </summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>How often a follower hears of changes.</summary>
public static class AlertFrequencies
{
    public const string Immediate = "immediate";
    public const string Daily = "daily";

    public static bool IsValid(string value) => value is Immediate or Daily;
}

/// <summary>A user follows a list or an item (NTF-03).</summary>
public class Follow : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>Null when the whole list is followed.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>An <see cref="AlertFrequencies"/> value.</summary>
    public string Frequency { get; set; } = AlertFrequencies.Immediate;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>A change collected for a user's next daily digest.</summary>
public class DigestEntry : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public string Change { get; set; } = "";

    public string? Title { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>
/// A change subscription (API-06): an API client gets a signed POST to <see cref="NotificationUrl"/> when items of a
/// list (or one item) are created, updated or deleted, if the owner can read them.
/// </summary>
public class ChangeSubscription : ITenantOwned, IAuditable, IVersioned
{
    public const int MaxClientState = 255;

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The owner: notifications only name items this user can read.</summary>
    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>Null when the whole list is watched.</summary>
    public Guid? ItemId { get; set; }

    /// <summary><c>created</c>, <c>updated</c>, <c>deleted</c>, joined with <c>,</c>.</summary>
    public string ChangeTypes { get; set; } = "";

    public string NotificationUrl { get; set; } = "";

    /// <summary>Echoed in every notification so the receiver can check it.</summary>
    public string? ClientState { get; set; }

    /// <summary>The signing secret, protected with ASP.NET Core data protection.</summary>
    public string Secret { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>A change to post to a subscription's URL; retried with backoff.</summary>
public class ChangeDelivery : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SubscriptionId { get; set; }

    /// <summary>The item event (one delivery per subscription and event).</summary>
    public Guid EventId { get; set; }

    public string ChangeType { get; set; } = "";

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>A <see cref="DeliveryStatuses"/> value.</summary>
    public string Status { get; set; } = DeliveryStatuses.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public string? LastError { get; set; }
}

#pragma warning restore CA1852

/// <summary>Notifications, settings, follows and change subscriptions. Query rules as in every module (ADR-0039).</summary>
public class NotificationsDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Notification> Notifications { get; set; } = null!;

    public DbSet<NotificationSettings> Settings { get; set; } = null!;

    public DbSet<WebhookDelivery> Deliveries { get; set; } = null!;

    public DbSet<Follow> Follows { get; set; } = null!;

    public DbSet<DigestEntry> Digest { get; set; } = null!;

    public DbSet<ChangeSubscription> ChangeSubscriptions { get; set; } = null!;

    public DbSet<ChangeDelivery> ChangeDeliveries { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("notifications");
            b.Property(n => n.Type).HasMaxLength(100);
            b.Property(n => n.Title).HasMaxLength(300);
            b.Property(n => n.Body).HasMaxLength(4000);
            b.Property(n => n.DeduplicationKey).HasMaxLength(200);
            b.HasIndex(n => new { n.TenantId, n.UserId, n.InInbox, n.ReadAt });
            b.HasIndex(n => new { n.TenantId, n.UserId, n.DeduplicationKey }).IsUnique();
        });
        modelBuilder.Entity<NotificationSettings>(b =>
        {
            b.ToTable("notification_settings");
            b.Property(s => s.WebhookUrl).HasMaxLength(2000);
            b.Property(s => s.WebhookSecret).HasMaxLength(1000);
            b.HasIndex(s => new { s.TenantId, s.UserId }).IsUnique();
        });
        modelBuilder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("webhook_deliveries");
            b.Property(d => d.Status).HasMaxLength(20);
            b.Property(d => d.LastError).HasMaxLength(500);
            b.HasIndex(d => new { d.TenantId, d.Status });
            b.HasIndex(d => new { d.TenantId, d.NotificationId });
        });
        modelBuilder.Entity<Follow>(b =>
        {
            b.ToTable("follows");
            b.Property(s => s.Frequency).HasMaxLength(20);
            b.HasIndex(s => new { s.TenantId, s.UserId, s.ListId, s.ItemId }).IsUnique();
            b.HasIndex(s => new { s.TenantId, s.ListId, s.ItemId });
        });
        modelBuilder.Entity<DigestEntry>(b =>
        {
            b.ToTable("digest_entries");
            b.Property(d => d.Change).HasMaxLength(20);
            b.Property(d => d.Title).HasMaxLength(300);
            b.HasIndex(d => new { d.TenantId, d.UserId });
        });
        modelBuilder.Entity<ChangeSubscription>(b =>
        {
            b.ToTable("change_subscriptions");
            b.Property(c => c.ChangeTypes).HasMaxLength(100);
            b.Property(c => c.NotificationUrl).HasMaxLength(2000);
            b.Property(c => c.ClientState).HasMaxLength(ChangeSubscription.MaxClientState);
            b.Property(c => c.Secret).HasMaxLength(1000);
            b.HasIndex(c => new { c.TenantId, c.ListId });
            b.HasIndex(c => new { c.TenantId, c.UserId });
        });
        modelBuilder.Entity<ChangeDelivery>(b =>
        {
            b.ToTable("change_deliveries");
            b.Property(d => d.Status).HasMaxLength(20);
            b.Property(d => d.ChangeType).HasMaxLength(20);
            b.Property(d => d.LastError).HasMaxLength(500);
            b.HasIndex(d => new { d.TenantId, d.SubscriptionId, d.EventId }).IsUnique();
            b.HasIndex(d => new { d.TenantId, d.Status });
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<NotificationsDbContext>());
}
