using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Notifications.Data;

namespace PaperDotNet.Notifications.Features;

public sealed class NotificationsOptions
{
    public const string Section = "Notifications";

    /// <summary>Allow webhook URLs with plain <c>http</c> (default: https only).</summary>
    public bool AllowHttpWebhooks { get; set; }

    /// <summary>Allow webhooks to private, loopback and link-local addresses (default: no, against SSRF).</summary>
    public bool AllowPrivateNetworkWebhooks { get; set; }

    public TimeSpan WebhookTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Channel choice for one notification type.</summary>
public sealed record ChannelChoice(bool InApp = true, bool Webhook = true);

/// <summary>Reads and applies a user's settings: channels per type, quiet hours, digest hour.</summary>
internal static class SettingsRules
{
    public static Dictionary<string, ChannelChoice> Channels(NotificationSettings? settings) =>
        settings is null ? [] : JsonSerializer.Deserialize(settings.Channels, NotificationsJson.Default.DictionaryStringChannelChoice) ?? [];

    public static string Serialize(Dictionary<string, ChannelChoice> channels) => JsonSerializer.Serialize(channels, NotificationsJson.Default.DictionaryStringChannelChoice);

    public static ChannelChoice For(NotificationSettings? settings, string type) => Channels(settings).GetValueOrDefault(type) ?? new ChannelChoice();

    /// <summary>When a webhook may be sent: now, or the end of the quiet hours (in the user's time zone).</summary>
    public static DateTimeOffset NextAllowed(NotificationSettings? settings, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (settings?.QuietHoursStart is not { } start || settings.QuietHoursEnd is not { } end || start == end)
        {
            return now;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var time = TimeOnly.FromDateTime(local.DateTime);
        var quiet = start < end ? time >= start && time < end : time >= start || time < end;
        if (!quiet)
        {
            return now;
        }

        var endDate = DateOnly.FromDateTime(local.DateTime);
        if (start > end && time >= start)
        {
            endDate = endDate.AddDays(1); // Overnight window: it ends tomorrow.
        }

        var endLocal = endDate.ToDateTime(end);
        return new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal));
    }

    public static string? Truncate(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}

/// <summary>
/// Creates notifications (NTF-01): inbox rows, live events and webhook deliveries per the users' channel choices;
/// quiet hours postpone webhooks (NTF-05).
/// </summary>
internal sealed class NotificationSender(NotificationsDbContext db, ILiveEvents live, IUserPreferences preferences, TimeProvider time) : INotificationSender
{
    public async Task<int> SendAsync(Guid tenantId, NotificationMessage message, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        var key = message.DeduplicationKey;
        var users = new List<Guid>();
        var settings = new Dictionary<Guid, NotificationSettings>();
        foreach (var user in userIds.Distinct())
        {
            var id = user;
            if (key is not null && await context.Notifications.AnyAsync(n => n.TenantId == tenant && n.UserId == id && n.DeduplicationKey == key, ct))
            {
                continue;
            }

            users.Add(id);
            if (await context.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenant && s.UserId == id, ct) is { } found)
            {
                settings[id] = found;
            }
        }

        if (users.Count == 0)
        {
            return 0;
        }

        var webhookUsers = settings.Values.Where(s => s.WebhookUrl is not null).Select(s => s.UserId).ToList();
        var zones = webhookUsers.Count == 0 ? new Dictionary<Guid, PreferenceValues>() : await preferences.GetAsync(tenant, webhookUsers, ct);
        var now = time.GetUtcNow();
        var created = new List<Notification>();
        foreach (var user in users)
        {
            var userSettings = settings.GetValueOrDefault(user);
            var channels = SettingsRules.For(userSettings, message.Type);
            var notification = new Notification
            {
                Id = Ids.New(),
                TenantId = tenant,
                UserId = user,
                Type = SettingsRules.Truncate(message.Type, 100)!,
                Title = SettingsRules.Truncate(message.Title, 300)!,
                Body = SettingsRules.Truncate(message.Body, 4000),
                WorkspaceId = message.Link?.WorkspaceId,
                ListId = message.Link?.ListId,
                ItemId = message.Link?.ItemId,
                DeduplicationKey = key,
                InInbox = channels.InApp,
                CreatedAt = now,
            };
            context.Notifications.Add(notification);
            created.Add(notification);
            if (channels.Webhook && userSettings?.WebhookUrl is not null)
            {
                context.Deliveries.Add(new WebhookDelivery
                {
                    Id = Ids.New(),
                    TenantId = tenant,
                    NotificationId = notification.Id,
                    UserId = user,
                    NextAttemptAt = SettingsRules.NextAllowed(userSettings, zones[user].Zone, now),
                });
            }
        }

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Sent concurrently with the same deduplication key: the other send won.
            context.ChangeTracker.Clear();
            return 0;
        }

        foreach (var notification in created.Where(n => n.InInbox))
        {
            live.Publish(new LiveEvent("notification", tenant, notification.UserId,
                JsonSerializer.SerializeToNode(NotificationResponse.From(notification), NotificationsJson.Default.NotificationResponse)!));
        }

        return created.Count;
    }
}

/// <summary>Signed posts to webhooks: user channels (NTF-04) and change subscriptions (API-06).</summary>
internal static class WebhookPosts
{
    /// <summary>Retry delays after the first, second, … failed attempt; then the delivery fails.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];

    public const string SecretPurpose = "PaperDotNet.Notifications.WebhookSecret";

    public static string NewSecret() => "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary><c>sha256=HMAC(secret, "{timestamp}.{body}")</c>.</summary>
    public static string Sign(string secret, string timestamp, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    /// <summary>Posts one signed body; returns an error text or null on success (2xx).</summary>
    public static async Task<string?> PostAsync(HttpClient client, string url, string secret, string eventName, Guid deliveryId, string body, TimeProvider time, CancellationToken ct)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-PaperDotNet-Event", eventName);
        request.Headers.Add("X-PaperDotNet-Delivery", deliveryId.ToString());
        request.Headers.Add("X-PaperDotNet-Timestamp", timestamp);
        request.Headers.Add("X-PaperDotNet-Signature", Sign(secret, timestamp, body));
        try
        {
            using var response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (HttpRequestException ex)
        {
            return ex.Message;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Timed out.";
        }
    }
}

/// <summary>
/// Posts pending webhook deliveries (NTF-04), signed with the user's secret: header
/// <c>X-PaperDotNet-Signature: sha256=HMAC(secret, "{timestamp}.{body}")</c>. Failures are retried with backoff
/// (1 min … 12 h, six attempts).
/// </summary>
internal sealed partial class WebhookDispatcher(
    NotificationsDbContext db, WebhookHttp http, IDataProtectionProvider protection, TimeProvider time,
    IUserPreferences preferences, ILogger<WebhookDispatcher> logger) : ITenantRecurringJob
{
    public const string Name = "notifications.webhooks";
    public const string Schedule = "*/20 * * * * *";

    /// <summary>Service key of the <see cref="HttpMessageHandler"/> webhooks are sent through.</summary>
    public const string HandlerKey = "PaperDotNet.Notifications.Webhook";
    private const int BatchSize = 100;

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var pending = DeliveryStatuses.Pending;
        var ct = cancellationToken;
        var now = time.GetUtcNow();
        // SQLite cannot compare DateTimeOffset values in SQL: the pending deliveries are filtered here.
        var due = (await context.Deliveries.Where(d => d.TenantId == tenant && d.Status == pending).ToListAsync(ct))
            .Where(d => d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(BatchSize)
            .ToList();
        if (due.Count == 0)
        {
            return;
        }

        var protector = protection.CreateProtector(WebhookPosts.SecretPurpose);
        var zones = await preferences.GetAsync(tenant, [.. due.Select(d => d.UserId).Distinct()], ct);
        foreach (var delivery in due)
        {
            var notificationId = delivery.NotificationId;
            var userId = delivery.UserId;
            var notification = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.TenantId == tenant && n.Id == notificationId, ct);
            var settings = await context.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenant && s.UserId == userId, ct);
            if (notification is null || settings?.WebhookUrl is null || settings.WebhookSecret is null)
            {
                delivery.Status = DeliveryStatuses.Failed;
                delivery.LastError = "The notification or the webhook no longer exists.";
                continue;
            }

            delivery.Attempts++;
            var body = JsonSerializer.Serialize(new WebhookBody(delivery.Id, tenant, NotificationResponse.From(notification)), NotificationsJson.Default.WebhookBody);
            var error = await WebhookPosts.PostAsync(http.Client, settings.WebhookUrl, protector.Unprotect(settings.WebhookSecret), "notification", delivery.Id, body, time, ct);
            if (error is null)
            {
                delivery.Status = DeliveryStatuses.Delivered;
                delivery.DeliveredAt = time.GetUtcNow();
                delivery.LastError = null;
                continue;
            }

            LogDeliveryFailed(delivery.Id, error);
            delivery.LastError = SettingsRules.Truncate(error, 500);
            if (delivery.Attempts > WebhookPosts.Backoff.Length)
            {
                delivery.Status = DeliveryStatuses.Failed;
            }
            else
            {
                delivery.NextAttemptAt = SettingsRules.NextAllowed(settings, zones[delivery.UserId].Zone, time.GetUtcNow() + WebhookPosts.Backoff[delivery.Attempts - 1]);
            }
        }

        await context.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} failed: {Error}")]
    private partial void LogDeliveryFailed(Guid deliveryId, string error);
}

/// <summary>The body of a user webhook post.</summary>
public sealed record WebhookBody(Guid DeliveryId, Guid TenantId, NotificationResponse Notification);

/// <summary>
/// The webhook HTTP client. Deliberately not an <c>IHttpClientFactory</c> client: webhooks have their own backoff, and
/// the connection goes through <see cref="WebhookNetwork"/>.
/// </summary>
internal sealed class WebhookHttp(HttpClient client) : IDisposable
{
    public HttpClient Client { get; } = client;

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Connects webhook requests only to public addresses (SSRF guard): the host is resolved at connect time and loopback,
/// private, link-local, CGNAT and unique-local addresses are refused unless allowed.
/// </summary>
internal static class WebhookNetwork
{
    public static SocketsHttpHandler CreateHandler(IOptions<NotificationsOptions> options) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = addresses.Where(a => options.Value.AllowPrivateNetworkWebhooks || IsPublic(a)).ToList();
            if (allowed.Count == 0)
            {
                throw new HttpRequestException($"The webhook host '{context.DnsEndPoint.Host}' does not resolve to a public address.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed.ToArray(), context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast);
        }

        var b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 0 || b[0] >= 224
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }
}
