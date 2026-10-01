using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Notifications.Data;

namespace PaperDotNet.Notifications.Features;

/// <summary>
/// Alerts for followed lists and items (NTF-03), a Wolverine handler generated ahead of time: each change notifies
/// followers other than the person who made it, only if they can read the item now; immediately or in the daily digest.
/// Idempotent: the event id is the deduplication key of the alert and of digest entries.
/// </summary>
public static class AlertSubscriber
{
    public static Task Handle(ItemAdded e, NotificationsDbContext db, IListItemStore items, INotificationSender sender, CancellationToken cancellationToken) =>
        AlertAsync(e, "added", db, items, sender, cancellationToken);

    public static Task Handle(ItemUpdated e, NotificationsDbContext db, IListItemStore items, INotificationSender sender, CancellationToken cancellationToken) =>
        AlertAsync(e, "updated", db, items, sender, cancellationToken);

    public static Task Handle(ItemDeleted e, NotificationsDbContext db, IListItemStore items, INotificationSender sender, CancellationToken cancellationToken) =>
        AlertAsync(e, "deleted", db, items, sender, cancellationToken);

    /// <summary>Removes follows and change subscriptions of permanently deleted items.</summary>
    public static async Task Handle(ItemPurged e, NotificationsDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = e.TenantId;
        var itemId = e.ItemId;
        var ct = cancellationToken;
        db.Follows.RemoveRange(await db.Follows.Where(s => s.TenantId == tenant && s.ItemId == itemId).ToListAsync(ct));
        foreach (var subscription in await db.ChangeSubscriptions.Where(s => s.TenantId == tenant && s.ItemId == itemId).ToListAsync(ct))
        {
            var subscriptionId = subscription.Id;
            db.ChangeDeliveries.RemoveRange(await db.ChangeDeliveries.Where(d => d.TenantId == tenant && d.SubscriptionId == subscriptionId).ToListAsync(ct));
            db.ChangeSubscriptions.Remove(subscription);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task AlertAsync(ItemEvent change, string kind, NotificationsDbContext database, IListItemStore items, INotificationSender sender, CancellationToken cancellationToken)
    {
        if (change.IsFolder)
        {
            return;
        }

        var db = database;
        var tenant = change.TenantId;
        var listId = change.ListId;
        var itemId = change.ItemId;
        var ct = cancellationToken;
        var followers = (await db.Follows.AsNoTracking().Where(s => s.TenantId == tenant && s.ListId == listId).ToListAsync(ct))
            .Where(s => (s.ItemId == null || s.ItemId == itemId) && s.UserId != change.UserId)
            .GroupBy(s => s.UserId)
            .Select(g => g.OrderBy(s => s.Frequency == AlertFrequencies.Immediate ? 0 : 1).First()) // Immediate wins over daily.
            .ToList();
        if (followers.Count == 0)
        {
            return;
        }

        var system = items.AsSystem(new ChangeActor(tenant, null));
        var title = (await system.GetAsync(change.WorkspaceId, listId, itemId, ct))?.Fields["title"]?.GetValue<string>() ?? change.Title;
        var list = await system.GetListAsync(change.WorkspaceId, listId, ct);
        var immediate = new List<Guid>();
        var eventKey = change.EventId.ToString("N");
        foreach (var follower in followers)
        {
            var reader = items.ActingAs(new ChangeActor(tenant, follower.UserId));
            var canRead = kind == "deleted"
                ? await reader.GetListAsync(change.WorkspaceId, listId, ct) is not null
                : await reader.GetAsync(change.WorkspaceId, listId, itemId, ct) is not null;
            if (!canRead)
            {
                continue;
            }

            if (follower.Frequency == AlertFrequencies.Immediate)
            {
                immediate.Add(follower.UserId);
                continue;
            }

            // The id of a digest entry comes from the event and the follower, so a redelivered event adds nothing.
            var entryId = DigestEntryId(eventKey, follower.UserId);
            if (!await db.Digest.AnyAsync(d => d.TenantId == tenant && d.Id == entryId, ct))
            {
                db.Digest.Add(new DigestEntry
                {
                    Id = entryId,
                    TenantId = tenant,
                    UserId = follower.UserId,
                    WorkspaceId = change.WorkspaceId,
                    ListId = listId,
                    ItemId = itemId,
                    Change = kind,
                    Title = SettingsRules.Truncate(title, 300),
                    At = change.OccurredAt,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        if (immediate.Count > 0)
        {
            await sender.SendAsync(tenant,
                new NotificationMessage(NotificationTypes.ItemChanged, $"{title ?? "An item"} was {kind}", list is null ? null : $"In {list.Name}.",
                    new NotificationLink(change.WorkspaceId, listId, kind == "deleted" ? null : itemId), $"alert:{eventKey}"),
                immediate, ct);
        }
    }

    private static Guid DigestEntryId(string eventKey, Guid userId)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{eventKey}:{userId:N}"), hash);
        return new Guid(hash[..16]);
    }
}

/// <summary>A user or group was deleted: their notifications, settings and follows go.</summary>
public static class NotificationCleanupSubscriber
{
    public static async Task Handle(PrincipalDeleted e, NotificationsDbContext database, CancellationToken cancellationToken)
    {
        if (e.IsGroup)
        {
            return;
        }

        var db = database;
        var tenant = e.TenantId;
        var user = e.PrincipalId;
        var ct = cancellationToken;
        db.Deliveries.RemoveRange(await db.Deliveries.Where(d => d.TenantId == tenant && d.UserId == user).ToListAsync(ct));
        db.Notifications.RemoveRange(await db.Notifications.Where(n => n.TenantId == tenant && n.UserId == user).ToListAsync(ct));
        db.Settings.RemoveRange(await db.Settings.Where(s => s.TenantId == tenant && s.UserId == user).ToListAsync(ct));
        db.Follows.RemoveRange(await db.Follows.Where(s => s.TenantId == tenant && s.UserId == user).ToListAsync(ct));
        db.Digest.RemoveRange(await db.Digest.Where(d => d.TenantId == tenant && d.UserId == user).ToListAsync(ct));
        foreach (var subscription in await db.ChangeSubscriptions.Where(s => s.TenantId == tenant && s.UserId == user).ToListAsync(ct))
        {
            var subscriptionId = subscription.Id;
            db.ChangeDeliveries.RemoveRange(await db.ChangeDeliveries.Where(d => d.TenantId == tenant && d.SubscriptionId == subscriptionId).ToListAsync(ct));
            db.ChangeSubscriptions.Remove(subscription);
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Sends each user one digest per day of the changes collected for them (NTF-03, NTF-05), at their digest hour in their
/// time zone. Runs hourly.
/// </summary>
public sealed class DigestJob(NotificationsDbContext db, INotificationSender sender, IUserPreferences preferences, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "notifications.digest";
    public const string Schedule = "0 * * * *";
    private const int MaxLines = 20;

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        var now = time.GetUtcNow();
        var users = (await context.Digest.AsNoTracking().Where(d => d.TenantId == tenant).Select(d => d.UserId).ToListAsync(ct)).Distinct().ToList();
        if (users.Count == 0)
        {
            return;
        }

        var zones = await preferences.GetAsync(tenant, users, ct);
        foreach (var user in users)
        {
            var userId = user;
            var settings = await context.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenant && s.UserId == userId, ct);
            var local = TimeZoneInfo.ConvertTime(now, zones[user].Zone);
            if (local.Hour == (settings?.DigestHour ?? 7))
            {
                await SendAsync(tenant, user, DateOnly.FromDateTime(local.DateTime), now, ct);
            }
        }
    }

    /// <summary>Sends the user's digest now (also used by tests) and clears the collected changes.</summary>
    public async Task SendAsync(Guid tenantId, Guid userId, DateOnly day, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var user = userId;
        var ct = cancellationToken;
        // SQLite cannot compare DateTimeOffset values in SQL: the user's (few) entries are filtered here.
        var entries = (await context.Digest.Where(d => d.TenantId == tenant && d.UserId == user).ToListAsync(ct))
            .Where(d => d.At <= until)
            .OrderBy(d => d.At)
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var body = new StringBuilder();
        foreach (var entry in entries.Take(MaxLines))
        {
            body.Append("• ").Append(entry.Title ?? "An item").Append(" was ").Append(entry.Change).Append('\n');
        }

        if (entries.Count > MaxLines)
        {
            body.Append(CultureInfo.InvariantCulture, $"… and {entries.Count - MaxLines} more.");
        }

        await sender.SendAsync(tenant,
            new NotificationMessage(NotificationTypes.Digest, $"{entries.Count} changes in what you follow", body.ToString().TrimEnd(), null, $"digest:{day:yyyy-MM-dd}"),
            [user], ct);
        context.Digest.RemoveRange(entries);
        await context.SaveChangesAsync(ct);
    }
}
