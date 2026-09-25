namespace PaperDotNet.Notifications.Contracts;

/// <summary>Well-known notification types (users choose channels per type, NTF-05).</summary>
public static class NotificationTypes
{
    public const string Reminder = "reminder";
    public const string ItemChanged = "itemChanged";
    public const string Digest = "digest";
    public const string System = "system";
    public const string Automation = "automation";
}

/// <summary>What a notification points to.</summary>
public sealed record NotificationLink(Guid WorkspaceId, Guid ListId, Guid? ItemId);

/// <summary>
/// A notification. <see cref="DeduplicationKey"/> makes sending idempotent per user (e.g.
/// <c>reminder:task:{id}:{date}</c>): a second message with the same key is ignored.
/// </summary>
public sealed record NotificationMessage(string Type, string Title, string? Body = null, NotificationLink? Link = null, string? DeduplicationKey = null);

/// <summary>
/// Sends notifications to users of the current tenant (NTF-01…05): into their inbox, as live events,
/// and to the channels they chose (webhook), honoring quiet hours. Extensions define their own types
/// (e.g. <c>acme.invoices.approval</c>).
/// </summary>
public interface INotificationSender
{
    /// <summary>Returns the number of users the notification was created for (duplicates are skipped).</summary>
    Task<int> SendAsync(NotificationMessage message, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
