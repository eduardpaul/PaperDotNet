using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Notifications.Data;

namespace PaperDotNet.Notifications.Features;

public sealed record NotificationResponse(
    Guid Id, string Type, string Title, string? Body, Guid? WorkspaceId, Guid? ListId, Guid? ItemId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt)
{
    internal static NotificationResponse From(Notification n) => new(n.Id, n.Type, n.Title, n.Body, n.WorkspaceId, n.ListId, n.ItemId, n.CreatedAt, n.ReadAt);
}

public sealed record UnreadCount(int Count);

/// <summary>Settings; <see cref="WebhookSecret"/> is only returned when a secret was created (shown once).</summary>
public sealed record SettingsResponse(
    IReadOnlyDictionary<string, ChannelChoice> Channels, string? WebhookUrl, TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd,
    int DigestHour, string? WebhookSecret = null)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

/// <summary>Replaces the settings. Channels not listed use the defaults (in-app and webhook on).</summary>
public sealed record SettingsRequest(
    IReadOnlyDictionary<string, ChannelChoice>? Channels, string? WebhookUrl, TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd, int? DigestHour);

public sealed record SubscriptionRequest(Guid WorkspaceId, Guid ListId, Guid? ItemId, AlertFrequency Frequency = AlertFrequency.Immediate);

public sealed record SubscriptionResponse(Guid Id, Guid WorkspaceId, Guid ListId, Guid? ItemId, AlertFrequency Frequency, DateTimeOffset CreatedAt)
{
    internal static SubscriptionResponse From(Subscription s) => new(s.Id, s.WorkspaceId, s.ListId, s.ItemId, s.Frequency, s.CreatedAt);
}

/// <summary>
/// The caller's notifications (NTF-01), settings (NTF-04, NTF-05) and follows (NTF-03). Everything is
/// per user: other users' notifications are never visible.
/// </summary>
internal static class NotificationEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var me = endpoints.MapV1Group("me", "Notifications");
        me.MapGet("/notifications", ListAsync).RequireScope(NotificationScopes.Read).WithName("ListNotifications");
        me.MapGet("/notifications/unreadCount", UnreadAsync).RequireScope(NotificationScopes.Read).WithName("CountUnreadNotifications");
        me.MapPost("/notifications/{id:guid}/read", MarkReadAsync).RequireScope(NotificationScopes.Write).WithName("MarkNotificationRead");
        me.MapPost("/notifications/read", MarkAllReadAsync).RequireScope(NotificationScopes.Write).WithName("MarkAllNotificationsRead");
        me.MapDelete("/notifications/{id:guid}", DeleteAsync).RequireScope(NotificationScopes.Write).WithName("DeleteNotification");

        me.MapGet("/notificationSettings", GetSettingsAsync).RequireScope(NotificationScopes.Read).WithName("GetNotificationSettings");
        me.MapPut("/notificationSettings", PutSettingsAsync).RequireScope(NotificationScopes.Write).WithName("ReplaceNotificationSettings");
        me.MapPost("/notificationSettings/webhookSecret", RotateSecretAsync).RequireScope(NotificationScopes.Write).WithName("RotateWebhookSecret");
        me.MapPost("/notificationSettings/test", TestAsync).RequireScope(NotificationScopes.Write).WithName("SendTestNotification");

        me.MapGet("/subscriptions", ListSubscriptionsAsync).RequireScope(NotificationScopes.Read).WithName("ListSubscriptions");
        me.MapPost("/subscriptions", FollowAsync).RequireScope(NotificationScopes.Write).WithName("CreateSubscription");
        me.MapDelete("/subscriptions/{id:guid}", UnfollowAsync).RequireScope(NotificationScopes.Write).WithName("DeleteSubscription");
    }

    // ---- Inbox ------------------------------------------------------------------

    /// <summary>Newest first; <c>unreadOnly=true</c> filters. Keyset paging with <c>$top</c>/<c>$skiptoken</c>.</summary>
    private static async Task<Ok<Page<NotificationResponse>>> ListAsync(bool? unreadOnly, HttpRequest http, NotificationsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var page = PageRequest.From(http);
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == user.UserId && n.InInbox);
        if (unreadOnly == true)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        if (page.After is { } after)
        {
            query = query.Where(n => n.Id.CompareTo(after) < 0);
        }

        var items = await query.OrderByDescending(n => n.Id).Take(page.Top + 1).ToListAsync(ct);
        return TypedResults.Ok(Page.Create(items.Select(NotificationResponse.From).ToList(), page, http, n => n.Id));
    }

    private static async Task<Ok<UnreadCount>> UnreadAsync(NotificationsDbContext db, ICurrentUser user, CancellationToken ct) =>
        TypedResults.Ok(new UnreadCount(await db.Notifications.CountAsync(n => n.UserId == user.UserId && n.InInbox && n.ReadAt == null, ct)));

    private static async Task<Results<NoContent, NotFound>> MarkReadAsync(Guid id, NotificationsDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
        await db.Notifications.Where(n => n.Id == id && n.UserId == user.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, n => n.ReadAt ?? time.GetUtcNow()), ct) == 0
            ? TypedResults.NotFound()
            : TypedResults.NoContent();

    private static async Task<NoContent> MarkAllReadAsync(NotificationsDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await db.Notifications.Where(n => n.UserId == user.UserId && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, NotificationsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        await db.Deliveries.Where(d => d.NotificationId == id && d.UserId == user.UserId).ExecuteDeleteAsync(ct);
        return await db.Notifications.Where(n => n.Id == id && n.UserId == user.UserId).ExecuteDeleteAsync(ct) == 0
            ? TypedResults.NotFound()
            : TypedResults.NoContent();
    }

    // ---- Settings ----------------------------------------------------------------

    private static async Task<Ok<SettingsResponse>> GetSettingsAsync(NotificationsDbContext db, ICurrentUser user, HttpResponse response, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == user.UserId, ct);
        ETags.Set(response, settings?.Version ?? 0);
        return TypedResults.Ok(ToResponse(settings));
    }

    /// <summary>
    /// Replaces the settings. Setting a new webhook URL creates a signing secret, returned once in
    /// <c>webhookSecret</c>. Quiet hours and the digest hour are local times in the
    /// user's preferred time zone (<c>/v1.0/me/preferences</c>).
    /// </summary>
    private static async Task<Results<Ok<SettingsResponse>, ValidationProblem, ProblemHttpResult>> PutSettingsAsync(
        SettingsRequest request, NotificationsDbContext db, ICurrentUser user, IDataProtectionProvider protection,
        IOptions<NotificationsOptions> options, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "userRequired", "Settings belong to a user.");
        }

        var errors = new Dictionary<string, string[]>();
        if (request.WebhookUrl is { Length: > 0 } url && WebhookUrlError(url, options.Value) is { } urlError)
        {
            errors["webhookUrl"] = [urlError];
        }

        if (request.DigestHour is < 0 or > 23)
        {
            errors["digestHour"] = ["An hour from 0 to 23 is expected."];
        }

        if ((request.QuietHoursStart is null) != (request.QuietHoursEnd is null))
        {
            errors["quietHours"] = ["Give both quietHoursStart and quietHoursEnd, or neither."];
        }

        if (request.Channels is { } channels && (channels.Count > 100 || channels.Keys.Any(k => k.Length is 0 or > 100)))
        {
            errors["channels"] = ["Up to 100 notification types with names of 1 to 100 characters."];
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var settings = await db.Settings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (ETags.TryGetIfMatch(http, out var expected) && expected != (settings?.Version ?? 0))
        {
            return ApiErrors.PreconditionFailed();
        }

        if (settings is null)
        {
            settings = new NotificationSettings { Id = Ids.New(), UserId = userId };
            db.Settings.Add(settings);
        }

        var newUrl = string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : request.WebhookUrl.Trim();
        string? secret = null;
        if (newUrl is not null && (newUrl != settings.WebhookUrl || settings.WebhookSecret is null))
        {
            secret = NewSecret();
            settings.WebhookSecret = protection.CreateProtector(WebhookDispatcher.SecretPurpose).Protect(secret);
        }
        else if (newUrl is null)
        {
            settings.WebhookSecret = null;
        }

        settings.WebhookUrl = newUrl;
        settings.Channels = SettingsRules.Serialize(request.Channels ?? new Dictionary<string, ChannelChoice>());
        settings.QuietHoursStart = request.QuietHoursStart;
        settings.QuietHoursEnd = request.QuietHoursEnd;
        settings.DigestHour = request.DigestHour ?? 7;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(response, settings.Version);
        return TypedResults.Ok(ToResponse(settings) with { WebhookSecret = secret });
    }

    /// <summary>Creates a new webhook signing secret (returned once); the old one stops working.</summary>
    private static async Task<Results<Ok<SettingsResponse>, ProblemHttpResult>> RotateSecretAsync(
        NotificationsDbContext db, ICurrentUser user, IDataProtectionProvider protection, CancellationToken ct)
    {
        var settings = await db.Settings.FirstOrDefaultAsync(s => s.UserId == user.UserId, ct);
        if (settings?.WebhookUrl is null)
        {
            return ApiErrors.NotFound("No webhook is configured.");
        }

        var secret = NewSecret();
        settings.WebhookSecret = protection.CreateProtector(WebhookDispatcher.SecretPurpose).Protect(secret);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToResponse(settings) with { WebhookSecret = secret });
    }

    /// <summary>Sends a test notification to the caller through their channels.</summary>
    private static async Task<Results<Accepted, ProblemHttpResult>> TestAsync(INotificationSender sender, ICurrentUser user, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "userRequired", "Notifications go to users.");
        }

        await sender.SendAsync(new NotificationMessage(NotificationTypes.System, "Test notification", "Your notification channels work."), [userId], ct);
        return TypedResults.Accepted((string?)null);
    }

    internal static string? WebhookUrlError(string url, NotificationsOptions options) =>
        url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? "An absolute URL is expected."
            : uri.Scheme != Uri.UriSchemeHttps && !(options.AllowHttpWebhooks && uri.Scheme == Uri.UriSchemeHttp)
                ? "Webhooks must use https."
                : !string.IsNullOrEmpty(uri.UserInfo) ? "Credentials in the URL are not allowed." : null;

    private static string NewSecret() => "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static SettingsResponse ToResponse(NotificationSettings? s) => new(
        SettingsRules.Channels(s), s?.WebhookUrl, s?.QuietHoursStart, s?.QuietHoursEnd, s?.DigestHour ?? 7)
    { ETag = ETags.From(s?.Version ?? 0) };

    // ---- Subscriptions -----------------------------------------------------------------

    private static async Task<Ok<List<SubscriptionResponse>>> ListSubscriptionsAsync(NotificationsDbContext db, ICurrentUser user, CancellationToken ct) =>
        TypedResults.Ok((await db.Subscriptions.AsNoTracking().Where(s => s.UserId == user.UserId).OrderBy(s => s.CreatedAt).ToListAsync(ct))
            .Select(SubscriptionResponse.From).ToList());

    /// <summary>Follows a list or an item the caller can read (NTF-03); following again changes the frequency.</summary>
    private static async Task<Results<Ok<SubscriptionResponse>, ProblemHttpResult>> FollowAsync(
        SubscriptionRequest request, NotificationsDbContext db, IListItemStore items, ICurrentUser user, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "userRequired", "Subscriptions belong to a user.");
        }

        var visible = request.ItemId is { } itemId
            ? await items.GetAsync(request.WorkspaceId, request.ListId, itemId, ct) is not null
            : await items.GetListAsync(request.WorkspaceId, request.ListId, ct) is not null;
        if (!visible)
        {
            return ApiErrors.NotFound();
        }

        var subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId && s.ListId == request.ListId && s.ItemId == request.ItemId, ct);
        if (subscription is null)
        {
            subscription = new Subscription { Id = Ids.New(), UserId = userId, WorkspaceId = request.WorkspaceId, ListId = request.ListId, ItemId = request.ItemId };
            db.Subscriptions.Add(subscription);
        }

        subscription.Frequency = request.Frequency;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(SubscriptionResponse.From(subscription));
    }

    private static async Task<Results<NoContent, NotFound>> UnfollowAsync(Guid id, NotificationsDbContext db, ICurrentUser user, CancellationToken ct) =>
        await db.Subscriptions.Where(s => s.Id == id && s.UserId == user.UserId).ExecuteDeleteAsync(ct) == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
}
