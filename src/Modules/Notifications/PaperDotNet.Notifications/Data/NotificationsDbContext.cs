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

    /// <summary>Quiet hours (local time in <see cref="TimeZone"/>): webhook deliveries wait until they end.</summary>
    public TimeOnly? QuietHoursStart { get; set; }

    public TimeOnly? QuietHoursEnd { get; set; }

    public string TimeZone { get; set; } = "UTC";

    /// <summary>Hour of the day (in <see cref="TimeZone"/>) for daily digests.</summary>
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

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
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
            b.Property(s => s.TimeZone).HasMaxLength(64);
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
