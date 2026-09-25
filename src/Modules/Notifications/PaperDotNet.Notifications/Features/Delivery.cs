using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Dictionary<string, ChannelChoice> Channels(NotificationSettings? settings) =>
        settings is null ? [] : JsonSerializer.Deserialize<Dictionary<string, ChannelChoice>>(settings.Channels, Json) ?? [];

    public static string Serialize(IReadOnlyDictionary<string, ChannelChoice> channels) => JsonSerializer.Serialize(channels, Json);

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
}

/// <summary>
/// Creates notifications (NTF-01): inbox rows, live events and webhook deliveries per the users'
/// channel choices; quiet hours postpone webhooks (NTF-05).
/// </summary>
internal sealed class NotificationSender(
    NotificationsDbContext db, ILiveEvents live, ITenantContext tenant, IUserPreferences preferences, TimeProvider time) : INotificationSender
{
    public async Task<int> SendAsync(NotificationMessage message, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var users = userIds.Distinct().ToList();
        if (users.Count == 0)
        {
            return 0;
        }

        if (message.DeduplicationKey is { } key)
        {
            var already = await db.Notifications.Where(n => users.Contains(n.UserId) && n.DeduplicationKey == key).Select(n => n.UserId).ToListAsync(cancellationToken);
            users = users.Except(already).ToList();
        }

        var settings = await db.Settings.AsNoTracking().Where(s => users.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId, cancellationToken);
        var webhookUsers = settings.Values.Where(s => s.WebhookUrl is not null).Select(s => s.UserId).ToList();
        var zones = webhookUsers.Count == 0 ? new Dictionary<Guid, PreferenceValues>() : await preferences.GetAsync(webhookUsers, cancellationToken);
        var now = time.GetUtcNow();
        var created = new List<Notification>();
        foreach (var user in users)
        {
            var userSettings = settings.GetValueOrDefault(user);
            var channels = SettingsRules.For(userSettings, message.Type);
            var notification = new Notification
            {
                Id = Ids.New(),
                UserId = user,
                Type = Truncate(message.Type, 100)!,
                Title = Truncate(message.Title, 300)!,
                Body = Truncate(message.Body, 4000),
                WorkspaceId = message.Link?.WorkspaceId,
                ListId = message.Link?.ListId,
                ItemId = message.Link?.ItemId,
                DeduplicationKey = message.DeduplicationKey,
                InInbox = channels.InApp,
                CreatedAt = now,
            };
            db.Notifications.Add(notification);
            created.Add(notification);
            if (channels.Webhook && userSettings?.WebhookUrl is not null)
            {
                db.Deliveries.Add(new WebhookDelivery
                {
                    Id = Ids.New(),
                    NotificationId = notification.Id,
                    UserId = user,
                    NextAttemptAt = SettingsRules.NextAllowed(userSettings, zones[user].Zone, now),
                });
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Sent concurrently with the same deduplication key: the other send won.
            db.ChangeTracker.Clear();
            return 0;
        }

        foreach (var notification in created.Where(n => n.InInbox))
        {
            live.Publish(new LiveEvent("notification", tenant.TenantId!.Value, notification.UserId, NotificationResponse.From(notification)));
        }

        return created.Count;
    }

    private static string? Truncate(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}

/// <summary>
/// Posts pending webhook deliveries (NTF-04), signed with the user's secret: header
/// <c>X-PaperDotNet-Signature: sha256=HMAC(secret, "{timestamp}.{body}")</c>. Failures are retried with
/// backoff (1 min … 12 h, six attempts).
/// </summary>
internal sealed partial class WebhookDispatcher(
    NotificationsDbContext db, WebhookHttp http, IDataProtectionProvider protection, TimeProvider time,
    ITenantContext tenant, IUserPreferences preferences, ILogger<WebhookDispatcher> logger) : ITenantRecurringJob
{
    public const string Name = "notifications.webhooks";
    public const string Schedule = "*/20 * * * * *";
    /// <summary>Service key of the <see cref="HttpMessageHandler"/> webhooks are sent through.</summary>
    public const string HandlerKey = "PaperDotNet.Notifications.Webhook";
    public const string SecretPurpose = "PaperDotNet.Notifications.WebhookSecret";
    private const int BatchSize = 100;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = await db.Deliveries.Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).Take(BatchSize).ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return;
        }

        var notificationIds = due.Select(d => d.NotificationId).ToList();
        var userIds = due.Select(d => d.UserId).Distinct().ToList();
        var notifications = await db.Notifications.AsNoTracking().Where(n => notificationIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, cancellationToken);
        var settings = await db.Settings.AsNoTracking().Where(s => userIds.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId, cancellationToken);
        var zones = await preferences.GetAsync(userIds, cancellationToken);
        var protector = protection.CreateProtector(SecretPurpose);
        var client = http.Client;
        foreach (var delivery in due)
        {
            var userSettings = settings.GetValueOrDefault(delivery.UserId);
            if (!notifications.TryGetValue(delivery.NotificationId, out var notification) || userSettings?.WebhookUrl is null || userSettings.WebhookSecret is null)
            {
                delivery.Status = DeliveryStatus.Failed;
                delivery.LastError = "The notification or the webhook no longer exists.";
                continue;
            }

            delivery.Attempts++;
            var error = await PostAsync(client, userSettings.WebhookUrl, protector.Unprotect(userSettings.WebhookSecret), delivery, notification, cancellationToken);
            if (error is null)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAt = time.GetUtcNow();
                delivery.LastError = null;
            }
            else
            {
                LogDeliveryFailed(delivery.Id, error);
                delivery.LastError = error.Length > 500 ? error[..500] : error;
                if (delivery.Attempts > Backoff.Length)
                {
                    delivery.Status = DeliveryStatus.Failed;
                }
                else
                {
                    delivery.NextAttemptAt = SettingsRules.NextAllowed(userSettings, zones[delivery.UserId].Zone, time.GetUtcNow() + Backoff[delivery.Attempts - 1]);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Posts one notification; returns an error text or null on success (2xx).</summary>
    private async Task<string?> PostAsync(HttpClient client, string url, string secret, WebhookDelivery delivery, Notification notification, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            deliveryId = delivery.Id,
            tenant = tenant.TenantIdentifier,
            notification = NotificationResponse.From(notification),
        }, WebhookJson);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-PaperDotNet-Event", "notification");
        request.Headers.Add("X-PaperDotNet-Delivery", delivery.Id.ToString());
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

    public static string Sign(string secret, string timestamp, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    private static readonly JsonSerializerOptions WebhookJson = new(JsonSerializerDefaults.Web);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} failed: {Error}")]
    private partial void LogDeliveryFailed(Guid deliveryId, string error);
}

/// <summary>
/// The webhook HTTP client. Deliberately not an <c>IHttpClientFactory</c> client: the host's default
/// resilience handler would retry failed posts in-process, while webhooks have their own backoff.
/// </summary>
internal sealed class WebhookHttp(HttpClient client) : IDisposable
{
    public HttpClient Client { get; } = client;

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Connects webhook requests only to public addresses (SSRF guard): the host is resolved at connect
/// time and loopback, private, link-local, CGNAT and unique-local addresses are refused unless allowed.
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
