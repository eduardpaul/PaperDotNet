using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Notifications.Data;

namespace PaperDotNet.Notifications.Features;

/// <summary>
/// Alerts for followed lists and items (NTF-03): each change notifies followers other than the person
/// who made it, only if they can read the item now; immediately or in the daily digest.
/// Idempotent: the event id is the deduplication key.
/// </summary>
internal sealed class AlertSubscriber(
    NotificationsDbContext db, IListItemStore items, INotificationSender sender, ITenantScopeFactory scopes, TimeProvider time)
    : IEventSubscriber<ItemAdded>, IEventSubscriber<ItemUpdated>, IEventSubscriber<ItemDeleted>
{
    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken) => AlertAsync(integrationEvent, "added", cancellationToken);

    public Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken) => AlertAsync(integrationEvent, "updated", cancellationToken);

    public Task HandleAsync(ItemDeleted integrationEvent, CancellationToken cancellationToken) => AlertAsync(integrationEvent, "deleted", cancellationToken);

    private async Task AlertAsync(ItemEvent change, string kind, CancellationToken ct)
    {
        if (change.IsFolder)
        {
            return;
        }

        var followers = (await db.Subscriptions.AsNoTracking()
                .Where(s => s.ListId == change.ListId && (s.ItemId == null || s.ItemId == change.ItemId) && s.UserId != change.UserId)
                .ToListAsync(ct))
            .GroupBy(s => s.UserId)
            .Select(g => g.OrderBy(s => s.Frequency).First()) // Immediate wins over daily.
            .ToList();
        if (followers.Count == 0)
        {
            return;
        }

        var system = items.AsSystem();
        var title = (await system.GetAsync(change.WorkspaceId, change.ListId, change.ItemId, ct))?.Fields["title"]?.GetValue<string>();
        var list = await system.GetListAsync(change.WorkspaceId, change.ListId, ct);
        var immediate = new List<Guid>();
        foreach (var follower in followers)
        {
            if (!await CanReadAsync(change, follower.UserId, kind, ct))
            {
                continue;
            }

            if (follower.Frequency == AlertFrequency.Immediate)
            {
                immediate.Add(follower.UserId);
            }
            else
            {
                db.Digest.Add(new DigestEntry
                {
                    Id = Ids.New(),
                    UserId = follower.UserId,
                    WorkspaceId = change.WorkspaceId,
                    ListId = change.ListId,
                    ItemId = change.ItemId,
                    Change = kind,
                    Title = title,
                    At = time.GetUtcNow(),
                });
            }
        }

        await db.SaveChangesAsync(ct);
        if (immediate.Count > 0)
        {
            var what = title ?? "An item";
            await sender.SendAsync(
                new NotificationMessage(NotificationTypes.ItemChanged, $"{what} was {kind}", list is null ? null : $"In {list.Name}.",
                    new NotificationLink(change.WorkspaceId, change.ListId, kind == "deleted" ? null : change.ItemId), $"alert:{change.EventId:N}"),
                immediate, ct);
        }
    }

    /// <summary>Whether the follower can read the item now (for deletions: the list).</summary>
    private async Task<bool> CanReadAsync(ItemEvent change, Guid userId, string kind, CancellationToken ct)
    {
        await using var scope = scopes.CreateScope(change.TenantId, change.TenantIdentifier, userId);
        var store = scope.ServiceProvider.GetRequiredService<IListItemStore>();
        return kind == "deleted"
            ? await store.GetListAsync(change.WorkspaceId, change.ListId, ct) is not null
            : await store.GetAsync(change.WorkspaceId, change.ListId, change.ItemId, ct) is not null;
    }
}

/// <summary>
/// Sends each user one digest per day of the changes collected for them (NTF-03, NTF-05), at their
/// digest hour in their time zone. Runs hourly.
/// </summary>
internal sealed class DigestJob(NotificationsDbContext db, INotificationSender sender, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "notifications.digest";
    public const string Schedule = "0 * * * *";
    private const int MaxLines = 20;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var users = await db.Digest.Select(d => d.UserId).Distinct().ToListAsync(cancellationToken);
        var settings = await db.Settings.AsNoTracking().Where(s => users.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId, cancellationToken);
        foreach (var user in users)
        {
            var userSettings = settings.GetValueOrDefault(user);
            var local = TimeZoneInfo.ConvertTime(now, SettingsRules.Zone(userSettings));
            if (local.Hour != (userSettings?.DigestHour ?? 7))
            {
                continue;
            }

            await SendAsync(user, DateOnly.FromDateTime(local.DateTime), now, cancellationToken);
        }
    }

    /// <summary>Sends the user's digest now (also used by tests) and clears the collected changes.</summary>
    public async Task SendAsync(Guid user, DateOnly day, DateTimeOffset until, CancellationToken ct)
    {
        var entries = await db.Digest.Where(d => d.UserId == user && d.At <= until).OrderBy(d => d.At).ToListAsync(ct);
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

        await sender.SendAsync(
            new NotificationMessage(NotificationTypes.Digest, $"{entries.Count} changes in what you follow", body.ToString().TrimEnd(), null,
                $"digest:{day:yyyy-MM-dd}"),
            [user], ct);
        db.Digest.RemoveRange(entries);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Removes follows and change subscriptions of permanently deleted items.</summary>
internal sealed class PurgedFollows(NotificationsDbContext db) : IEventSubscriber<ItemPurged>
{
    public async Task HandleAsync(ItemPurged integrationEvent, CancellationToken cancellationToken)
    {
        await db.Subscriptions.Where(s => s.ItemId == integrationEvent.ItemId).ExecuteDeleteAsync(cancellationToken);
        var changes = db.ChangeSubscriptions.Where(s => s.ItemId == integrationEvent.ItemId).Select(s => s.Id);
        await db.ChangeDeliveries.Where(d => changes.Contains(d.SubscriptionId)).ExecuteDeleteAsync(cancellationToken);
        await db.ChangeSubscriptions.Where(s => s.ItemId == integrationEvent.ItemId).ExecuteDeleteAsync(cancellationToken);
    }
}
