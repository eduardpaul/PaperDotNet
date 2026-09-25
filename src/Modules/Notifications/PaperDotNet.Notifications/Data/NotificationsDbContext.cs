using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;

namespace PaperDotNet.Notifications.Data;

/// <summary>A notification in a user's inbox (NTF-01).</summary>
[NotAudited]
public sealed class Notification : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public required string Type { get; set; }

    public required string Title { get; set; }

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
public sealed class NotificationSettings : ITenantOwned, IAuditable, IVersioned
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

    public uint Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

public enum DeliveryStatus
{
    Pending = 0,
    Delivered = 1,
    Failed = 2,
}

/// <summary>A notification to post to a user's webhook; retried with backoff.</summary>
[NotAudited]
public sealed class WebhookDelivery : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid NotificationId { get; set; }

    public Guid UserId { get; set; }

    public DeliveryStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public string? LastError { get; set; }
}

public enum AlertFrequency
{
    Immediate = 0,
    Daily = 1,
}

/// <summary>A user follows a list or an item (NTF-03).</summary>
public sealed class Subscription : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    /// <summary>Null when the whole list is followed.</summary>
    public Guid? ItemId { get; set; }

    public AlertFrequency Frequency { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>A change collected for a user's next daily digest.</summary>
[NotAudited]
public sealed class DigestEntry : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public required string Change { get; set; }

    public string? Title { get; set; }

    public DateTimeOffset At { get; set; }
}

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public const string Schema = "notifications";

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationSettings> Settings => Set<NotificationSettings>();

    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<DigestEntry> Digest => Set<DigestEntry>();

    public DbSet<ChangeSubscription> ChangeSubscriptions => Set<ChangeSubscription>();

    public DbSet<ChangeDelivery> ChangeDeliveries => Set<ChangeDelivery>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChangeSubscription>(b =>
        {
            b.ToTable("change_subscriptions");
            b.Property(c => c.NotificationUrl).HasMaxLength(2000);
            b.Property(c => c.ClientState).HasMaxLength(ChangeSubscription.MaxClientState);
            b.Property(c => c.Secret).HasMaxLength(1000);
            b.HasIndex(c => new { c.TenantId, c.ListId, c.ExpiresAt });
            b.HasIndex(c => new { c.TenantId, c.UserId });
        });
        modelBuilder.Entity<ChangeDelivery>(b =>
        {
            b.ToTable("change_deliveries");
            b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.ChangeType).HasMaxLength(20);
            b.Property(d => d.LastError).HasMaxLength(500);
            b.HasIndex(d => new { d.SubscriptionId, d.EventId }).IsUnique();
            b.HasIndex(d => new { d.TenantId, d.Status, d.NextAttemptAt });
        });
        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("notifications");
            b.Property(n => n.Type).HasMaxLength(100);
            b.Property(n => n.Title).HasMaxLength(300);
            b.Property(n => n.Body).HasMaxLength(4000);
            b.Property(n => n.DeduplicationKey).HasMaxLength(200);
            b.HasIndex(n => new { n.UserId, n.InInbox, n.ReadAt });
            b.HasIndex(n => new { n.UserId, n.DeduplicationKey }).IsUnique();
        });
        modelBuilder.Entity<NotificationSettings>(b =>
        {
            b.ToTable("settings");
            b.Property(s => s.WebhookUrl).HasMaxLength(2000);
            b.Property(s => s.WebhookSecret).HasMaxLength(1000);
            b.HasIndex(s => s.UserId).IsUnique();
        });
        modelBuilder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("webhook_deliveries");
            b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.LastError).HasMaxLength(500);
            b.HasIndex(d => new { d.Status, d.NextAttemptAt });
        });
        modelBuilder.Entity<Subscription>(b =>
        {
            b.ToTable("subscriptions");
            b.Property(s => s.Frequency).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(s => new { s.UserId, s.ListId, s.ItemId }).IsUnique();
            b.HasIndex(s => new { s.ListId, s.ItemId });
        });
        modelBuilder.Entity<DigestEntry>(b =>
        {
            b.ToTable("digest_entries");
            b.Property(d => d.Change).HasMaxLength(20);
            b.Property(d => d.Title).HasMaxLength(300);
            b.HasIndex(d => d.UserId);
        });
    }
}

/// <summary>
/// A change subscription (API-06): an API client gets a signed POST to <see cref="NotificationUrl"/>
/// when items of a list (or one item) are created, updated or deleted, if the owner can read them.
/// </summary>
public sealed class ChangeSubscription : ITenantOwned, IAuditable, IVersioned
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

    /// <summary><c>created</c>, <c>updated</c>, <c>deleted</c>.</summary>
    public List<string> ChangeTypes { get; set; } = [];

    public required string NotificationUrl { get; set; }

    /// <summary>Echoed in every notification so the receiver can check it.</summary>
    public string? ClientState { get; set; }

    /// <summary>The signing secret, protected with ASP.NET Core data protection.</summary>
    public required string Secret { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

/// <summary>A change to post to a subscription's URL; retried with backoff.</summary>
[NotAudited]
public sealed class ChangeDelivery : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SubscriptionId { get; set; }

    /// <summary>The item event (one delivery per subscription and event).</summary>
    public Guid EventId { get; set; }

    public required string ChangeType { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DeliveryStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public string? LastError { get; set; }
}
