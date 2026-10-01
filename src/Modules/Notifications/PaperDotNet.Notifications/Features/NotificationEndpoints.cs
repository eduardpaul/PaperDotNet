using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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
    Dictionary<string, ChannelChoice> Channels, string? WebhookUrl, TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd, int DigestHour,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WebhookSecret = null)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

/// <summary>Replaces the settings. Channels not listed use the defaults (in-app and webhook on).</summary>
public sealed record SettingsRequest(
    Dictionary<string, ChannelChoice>? Channels, string? WebhookUrl, TimeOnly? QuietHoursStart, TimeOnly? QuietHoursEnd, int? DigestHour);

/// <summary>Follow body; <c>frequency</c> is <c>immediate</c> (default) or <c>daily</c>.</summary>
public sealed record FollowRequest(Guid WorkspaceId, Guid ListId, Guid? ItemId, string? Frequency);

public sealed record FollowResponse(Guid Id, Guid WorkspaceId, Guid ListId, Guid? ItemId, string Frequency, DateTimeOffset CreatedAt)
{
    internal static FollowResponse From(Follow s) => new(s.Id, s.WorkspaceId, s.ListId, s.ItemId, s.Frequency, s.CreatedAt);
}

/// <summary>
/// The caller's notifications (NTF-01), settings (NTF-04, NTF-05) and follows (NTF-03). Everything is per user: other
/// users' notifications are never visible.
/// </summary>
internal static class NotificationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/v1.0/me").WithTags("Notifications");
        me.MapGet("/notifications", ListAsync).RequireScope(NotificationScopes.Read).WithName("ListNotifications")
            .WithDescription("Newest first; ?unreadOnly=true filters.");
        me.MapGet("/notifications/unreadCount", UnreadAsync).RequireScope(NotificationScopes.Read).WithName("CountUnreadNotifications");
        me.MapPost("/notifications/{id:guid}/read", MarkReadAsync).RequireScope(NotificationScopes.Write).WithName("MarkNotificationRead");
        me.MapPost("/notifications/read", MarkAllReadAsync).RequireScope(NotificationScopes.Write).WithName("MarkAllNotificationsRead");
        me.MapDelete("/notifications/{id:guid}", DeleteAsync).RequireScope(NotificationScopes.Write).WithName("DeleteNotification");

        me.MapGet("/notificationSettings", GetSettingsAsync).RequireScope(NotificationScopes.Read).WithName("GetNotificationSettings");
        me.MapPut("/notificationSettings", PutSettingsAsync).RequireScope(NotificationScopes.Write).WithName("ReplaceNotificationSettings")
            .WithDescription("A new webhook URL creates a signing secret, returned once in webhookSecret. Quiet hours and the digest hour are local times in the user's time zone.");
        me.MapPost("/notificationSettings/webhookSecret", RotateSecretAsync).RequireScope(NotificationScopes.Write).WithName("RotateWebhookSecret");
        me.MapPost("/notificationSettings/test", TestAsync).RequireScope(NotificationScopes.Write).WithName("SendTestNotification");

        me.MapGet("/subscriptions", ListFollowsAsync).RequireScope(NotificationScopes.Read).WithName("ListSubscriptions");
        me.MapPost("/subscriptions", FollowAsync).RequireScope(NotificationScopes.Write).WithName("CreateSubscription")
            .WithDescription("Follows a list or an item the caller can read; following again changes the frequency.");
        me.MapDelete("/subscriptions/{id:guid}", UnfollowAsync).RequireScope(NotificationScopes.Write).WithName("DeleteSubscription");
    }

    // ---- Inbox ------------------------------------------------------------------

    private static async Task<Ok<Page<NotificationResponse>>> ListAsync(
        HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, bool? unreadOnly,
        Caller caller, NotificationsDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        List<Notification> items;

        // Static queries only (precompiled): one per combination of options.
        if (unreadOnly == true)
        {
            items = page.After is { } before
                ? await db.Notifications.AsNoTracking().Where(n => n.TenantId == tenant && n.UserId == user && n.InInbox && n.ReadAt == null && n.Id.CompareTo(before) < 0).OrderByDescending(n => n.Id).Take(take).ToListAsync(ct)
                : await db.Notifications.AsNoTracking().Where(n => n.TenantId == tenant && n.UserId == user && n.InInbox && n.ReadAt == null).OrderByDescending(n => n.Id).Take(take).ToListAsync(ct);
        }
        else
        {
            items = page.After is { } before
                ? await db.Notifications.AsNoTracking().Where(n => n.TenantId == tenant && n.UserId == user && n.InInbox && n.Id.CompareTo(before) < 0).OrderByDescending(n => n.Id).Take(take).ToListAsync(ct)
                : await db.Notifications.AsNoTracking().Where(n => n.TenantId == tenant && n.UserId == user && n.InInbox).OrderByDescending(n => n.Id).Take(take).ToListAsync(ct);
        }

        return TypedResults.Ok(Page.Create([.. items.Select(NotificationResponse.From)], page, request, n => n.Id));
    }

    private static async Task<Ok<UnreadCount>> UnreadAsync(Caller caller, NotificationsDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var ct = cancellationToken;
        return TypedResults.Ok(new UnreadCount(await db.Notifications.CountAsync(n => n.TenantId == tenant && n.UserId == user && n.InInbox && n.ReadAt == null, ct)));
    }

    private static async Task<Results<NoContent, NotFound>> MarkReadAsync(Guid id, Caller caller, NotificationsDbContext database, TimeProvider time, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var notificationId = id;
        var ct = cancellationToken;
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.TenantId == tenant && n.Id == notificationId && n.UserId == user, ct);
        if (notification is null)
        {
            return TypedResults.NotFound();
        }

        notification.ReadAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> MarkAllReadAsync(Caller caller, NotificationsDbContext database, TimeProvider time, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var ct = cancellationToken;
        var now = time.GetUtcNow();
        foreach (var notification in await db.Notifications.Where(n => n.TenantId == tenant && n.UserId == user && n.ReadAt == null).ToListAsync(ct))
        {
            notification.ReadAt = now;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, Caller caller, NotificationsDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var notificationId = id;
        var ct = cancellationToken;
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.TenantId == tenant && n.Id == notificationId && n.UserId == user, ct);
        if (notification is null)
        {
            return TypedResults.NotFound();
        }

        db.Deliveries.RemoveRange(await db.Deliveries.Where(d => d.TenantId == tenant && d.NotificationId == notificationId).ToListAsync(ct));
        db.Notifications.Remove(notification);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---- Settings ----------------------------------------------------------------

    private static Task<NotificationSettings?> FindSettingsAsync(NotificationsDbContext database, Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var user = userId;
        var ct = cancellationToken;
        return db.Settings.FirstOrDefaultAsync(s => s.TenantId == tenant && s.UserId == user, ct);
    }

    private static async Task<Ok<SettingsResponse>> GetSettingsAsync(Caller caller, NotificationsDbContext db, HttpResponse response, CancellationToken ct)
    {
        var settings = await FindSettingsAsync(db, caller.TenantId, caller.UserId, ct);
        ETags.Set(response, settings?.Version ?? 0);
        return TypedResults.Ok(ToResponse(settings));
    }

    private static async Task<Results<Ok<SettingsResponse>, ValidationProblem, ProblemHttpResult>> PutSettingsAsync(
        SettingsRequest request, Caller caller, NotificationsDbContext db, IDataProtectionProvider protection,
        IOptions<NotificationsOptions> options, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
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

        var settings = await FindSettingsAsync(db, caller.TenantId, caller.UserId, ct);
        if (ETags.TryGetIfMatch(http, out var expected) && expected != (settings?.Version ?? 0))
        {
            return ApiErrors.PreconditionFailed();
        }

        if (settings is null)
        {
            settings = new NotificationSettings { Id = Ids.New(), TenantId = caller.TenantId, UserId = caller.UserId };
            db.Settings.Add(settings);
        }

        var newUrl = string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : request.WebhookUrl.Trim();
        string? secret = null;
        if (newUrl is not null && (newUrl != settings.WebhookUrl || settings.WebhookSecret is null))
        {
            secret = WebhookPosts.NewSecret();
            settings.WebhookSecret = protection.CreateProtector(WebhookPosts.SecretPurpose).Protect(secret);
        }
        else if (newUrl is null)
        {
            settings.WebhookSecret = null;
        }

        settings.WebhookUrl = newUrl;
        settings.Channels = SettingsRules.Serialize(request.Channels ?? []);
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
        Caller caller, NotificationsDbContext db, IDataProtectionProvider protection, CancellationToken ct)
    {
        var settings = await FindSettingsAsync(db, caller.TenantId, caller.UserId, ct);
        if (settings?.WebhookUrl is null)
        {
            return ApiErrors.NotFound("No webhook is configured.");
        }

        var secret = WebhookPosts.NewSecret();
        settings.WebhookSecret = protection.CreateProtector(WebhookPosts.SecretPurpose).Protect(secret);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToResponse(settings) with { WebhookSecret = secret });
    }

    /// <summary>Sends a test notification to the caller through their channels.</summary>
    private static async Task<Accepted> TestAsync(INotificationSender sender, Caller caller, CancellationToken ct)
    {
        await sender.SendAsync(caller.TenantId, new NotificationMessage(NotificationTypes.System, "Test notification", "Your notification channels work."), [caller.UserId], ct);
        return TypedResults.Accepted((string?)null);
    }

    internal static string? WebhookUrlError(string url, NotificationsOptions options) =>
        url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? "An absolute URL is expected."
            : uri.Scheme != Uri.UriSchemeHttps && !(options.AllowHttpWebhooks && uri.Scheme == Uri.UriSchemeHttp)
                ? "Webhooks must use https."
                : !string.IsNullOrEmpty(uri.UserInfo) ? "Credentials in the URL are not allowed." : null;

    private static SettingsResponse ToResponse(NotificationSettings? s) => new(
        SettingsRules.Channels(s), s?.WebhookUrl, s?.QuietHoursStart, s?.QuietHoursEnd, s?.DigestHour ?? 7)
    { ETag = ETags.From(s?.Version ?? 0) };

    // ---- Follows -------------------------------------------------------------------

    private static async Task<Ok<List<FollowResponse>>> ListFollowsAsync(Caller caller, NotificationsDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var ct = cancellationToken;
        var follows = await db.Follows.AsNoTracking().Where(s => s.TenantId == tenant && s.UserId == user).OrderBy(s => s.Id).ToListAsync(ct);
        return TypedResults.Ok(follows.Select(FollowResponse.From).ToList());
    }

    private static async Task<Results<Ok<FollowResponse>, ValidationProblem, ProblemHttpResult>> FollowAsync(
        FollowRequest request, Caller caller, NotificationsDbContext database, IListItemStore items, CancellationToken cancellationToken)
    {
        var frequency = request.Frequency ?? AlertFrequencies.Immediate;
        if (!AlertFrequencies.IsValid(frequency))
        {
            return ApiErrors.Validation("frequency", "Use immediate or daily.");
        }

        var visible = request.ItemId is { } itemId
            ? await items.GetAsync(request.WorkspaceId, request.ListId, itemId, cancellationToken) is not null
            : await items.GetListAsync(request.WorkspaceId, request.ListId, cancellationToken) is not null;
        if (!visible)
        {
            return ApiErrors.NotFound();
        }

        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var listId = request.ListId;
        var target = request.ItemId;
        var ct = cancellationToken;
        var follow = target is null
            ? await db.Follows.FirstOrDefaultAsync(s => s.TenantId == tenant && s.UserId == user && s.ListId == listId && s.ItemId == null, ct)
            : await db.Follows.FirstOrDefaultAsync(s => s.TenantId == tenant && s.UserId == user && s.ListId == listId && s.ItemId == target, ct);
        if (follow is null)
        {
            follow = new Follow { Id = Ids.New(), TenantId = tenant, UserId = user, WorkspaceId = request.WorkspaceId, ListId = listId, ItemId = target };
            db.Follows.Add(follow);
        }

        follow.Frequency = frequency;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(FollowResponse.From(follow));
    }

    private static async Task<Results<NoContent, NotFound>> UnfollowAsync(Guid id, Caller caller, NotificationsDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var followId = id;
        var ct = cancellationToken;
        var follow = await db.Follows.FirstOrDefaultAsync(s => s.TenantId == tenant && s.Id == followId && s.UserId == user, ct);
        if (follow is null)
        {
            return TypedResults.NotFound();
        }

        db.Follows.Remove(follow);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
