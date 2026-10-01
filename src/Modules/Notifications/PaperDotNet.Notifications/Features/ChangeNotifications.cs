using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Data;

namespace PaperDotNet.Notifications.Features;

/// <summary>
/// Create body. <c>resource</c> is <c>workspaces/{id}/lists/{id}/items</c> (the whole list) or <c>…/items/{id}</c> (one
/// item); <c>changeTypes</c> any of <c>created</c>, <c>updated</c>, <c>deleted</c>; <c>expirationDateTime</c> at most 30
/// days ahead (default: the maximum).
/// </summary>
public sealed record ChangeSubscriptionRequest(
    string? Resource, IReadOnlyList<string>? ChangeTypes, string? NotificationUrl, string? ClientState, DateTimeOffset? ExpirationDateTime);

/// <summary>Renewal body: a new expiry and optionally a new client state.</summary>
public sealed record ChangeSubscriptionUpdate(DateTimeOffset? ExpirationDateTime, string? ClientState);

/// <summary>A change subscription; <see cref="Secret"/> is only returned when it is created (shown once).</summary>
public sealed record ChangeSubscriptionResponse(
    Guid Id, string Resource, IReadOnlyList<string> ChangeTypes, string NotificationUrl, string? ClientState,
    DateTimeOffset ExpirationDateTime, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret = null)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }

    internal static ChangeSubscriptionResponse From(ChangeSubscription s) =>
        new(s.Id, ChangeNotifications.Resource(s.WorkspaceId, s.ListId, s.ItemId), ChangeNotifications.TypesOf(s), s.NotificationUrl, s.ClientState, s.ExpiresAt, s.CreatedAt)
        {
            ETag = ETags.From(s.Version),
        };
}

/// <summary>The body of a change notification (Graph shape): one change per post.</summary>
public sealed record ChangeNotificationBody([property: JsonPropertyName("value")] IReadOnlyList<ChangeNotificationValue> Value);

public sealed record ChangeNotificationValue(
    Guid SubscriptionId, string? ClientState, string ChangeType, string Resource, ChangeResourceData ResourceData,
    DateTimeOffset OccurredAt, DateTimeOffset SubscriptionExpirationDateTime, Guid TenantId);

public sealed record ChangeResourceData(Guid Id, Guid WorkspaceId, Guid ListId);

/// <summary>
/// Change notifications (API-06): API clients subscribe to changes of a list or item and receive signed POSTs (as for
/// user webhooks). The URL must answer a validation request first. Notifications carry ids only, and only for items the
/// subscription's owner can read.
/// </summary>
internal static partial class ChangeNotifications
{
    public const int MaxPerUser = 100;
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);
    public static readonly string[] Types = ["created", "updated", "deleted"];

    public static string Resource(Guid workspaceId, Guid listId, Guid? itemId) =>
        $"workspaces/{workspaceId}/lists/{listId}/items" + (itemId is { } id ? $"/{id}" : "");

    public static List<string> TypesOf(ChangeSubscription subscription) => [.. subscription.ChangeTypes.Split(',', StringSplitOptions.RemoveEmptyEntries)];

    [GeneratedRegex(@"^/?workspaces/(?<ws>[0-9a-fA-F-]{36})/lists/(?<list>[0-9a-fA-F-]{36})/items(/(?<item>[0-9a-fA-F-]{36}))?$")]
    private static partial Regex ResourcePattern();

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/changeSubscriptions").WithTags("Change notifications");
        group.MapGet("", ListAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("ListChangeSubscriptions");
        group.MapPost("", CreateAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("CreateChangeSubscription")
            .WithDescription("The URL must answer POST {url}?validationToken=… with 200 and the token as the body.");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("GetChangeSubscription");
        group.MapPatch("/{id:guid}", RenewAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("RenewChangeSubscription");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("DeleteChangeSubscription");
    }

    private static Task<ChangeSubscription?> FindAsync(NotificationsDbContext database, Caller caller, Guid id, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var subscriptionId = id;
        var ct = cancellationToken;
        return db.ChangeSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenant && s.Id == subscriptionId && s.UserId == user, ct);
    }

    /// <summary>The caller's subscriptions, newest first (including expired ones until they are cleaned up).</summary>
    private static async Task<Ok<Page<ChangeSubscriptionResponse>>> ListAsync(
        HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        Caller caller, NotificationsDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var subscriptions = page.After is { } before
            ? await db.ChangeSubscriptions.AsNoTracking().Where(s => s.TenantId == tenant && s.UserId == user && s.Id.CompareTo(before) < 0).OrderByDescending(s => s.Id).Take(take).ToListAsync(ct)
            : await db.ChangeSubscriptions.AsNoTracking().Where(s => s.TenantId == tenant && s.UserId == user).OrderByDescending(s => s.Id).Take(take).ToListAsync(ct);
        return TypedResults.Ok(Page.Create([.. subscriptions.Select(ChangeSubscriptionResponse.From)], page, request, s => s.Id));
    }

    private static async Task<Results<Ok<ChangeSubscriptionResponse>, ProblemHttpResult>> GetAsync(
        Guid id, Caller caller, NotificationsDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (await FindAsync(db, caller, id, ct) is not { } subscription)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, subscription.Version);
        return TypedResults.Ok(ChangeSubscriptionResponse.From(subscription));
    }

    private static async Task<Results<Created<ChangeSubscriptionResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        ChangeSubscriptionRequest request, Caller caller, NotificationsDbContext database, IListItemStore items, WebhookHttp http,
        IDataProtectionProvider protection, IOptions<NotificationsOptions> options, TimeProvider time, HttpResponse response, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var match = ResourcePattern().Match(request.Resource ?? "");
        Guid workspaceId = default, listId = default;
        Guid? itemId = null;
        if (!match.Success || !Guid.TryParse(match.Groups["ws"].Value, out workspaceId) || !Guid.TryParse(match.Groups["list"].Value, out listId))
        {
            errors["resource"] = ["Use workspaces/{id}/lists/{id}/items or workspaces/{id}/lists/{id}/items/{id}."];
        }
        else if (match.Groups["item"].Success)
        {
            if (Guid.TryParse(match.Groups["item"].Value, out var parsed))
            {
                itemId = parsed;
            }
            else
            {
                errors["resource"] = ["The item id is not valid."];
            }
        }

        var types = (request.ChangeTypes ?? []).Distinct().ToList();
        if (types.Count == 0 || types.Any(t => !Types.Contains(t)))
        {
            errors["changeTypes"] = ["Use one or more of created, updated, deleted."];
        }

        var urlError = request.NotificationUrl is null ? "A notification URL is required." : NotificationEndpoints.WebhookUrlError(request.NotificationUrl, options.Value);
        if (urlError is not null)
        {
            errors["notificationUrl"] = [urlError];
        }

        if (request.ClientState is { Length: > ChangeSubscription.MaxClientState })
        {
            errors["clientState"] = [$"Up to {ChangeSubscription.MaxClientState} characters."];
        }

        var now = time.GetUtcNow();
        var expires = request.ExpirationDateTime ?? now + MaxLifetime;
        if (ExpiryError(expires, now) is { } expiryError)
        {
            errors["expirationDateTime"] = [expiryError];
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var visible = itemId is { } item
            ? await items.GetAsync(workspaceId, listId, item, cancellationToken) is not null
            : await items.GetListAsync(workspaceId, listId, cancellationToken) is not null;
        if (!visible)
        {
            return ApiErrors.NotFound();
        }

        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var ct = cancellationToken;
        if (await db.ChangeSubscriptions.CountAsync(s => s.TenantId == tenant && s.UserId == user, ct) >= MaxPerUser)
        {
            return ApiErrors.Conflict("tooManySubscriptions", $"You can have up to {MaxPerUser} change subscriptions.");
        }

        if (await ValidateEndpointAsync(http.Client, request.NotificationUrl!, ct) is { } validationError)
        {
            return ApiErrors.Validation("notificationUrl", validationError);
        }

        var secret = WebhookPosts.NewSecret();
        var subscription = new ChangeSubscription
        {
            Id = Ids.New(),
            TenantId = tenant,
            UserId = user,
            WorkspaceId = workspaceId,
            ListId = listId,
            ItemId = itemId,
            ChangeTypes = string.Join(',', types),
            NotificationUrl = request.NotificationUrl!,
            ClientState = request.ClientState,
            Secret = protection.CreateProtector(WebhookPosts.SecretPurpose).Protect(secret),
            ExpiresAt = expires,
        };
        db.ChangeSubscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, subscription.Version);
        return TypedResults.Created($"/v1.0/changeSubscriptions/{subscription.Id}", ChangeSubscriptionResponse.From(subscription) with { Secret = secret });
    }

    /// <summary>Extends (or shortens) the subscription; needs <c>If-Match</c>.</summary>
    private static async Task<Results<Ok<ChangeSubscriptionResponse>, ValidationProblem, ProblemHttpResult>> RenewAsync(
        Guid id, ChangeSubscriptionUpdate request, Caller caller, NotificationsDbContext db, TimeProvider time,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (await FindAsync(db, caller, id, ct) is not { } subscription)
        {
            return ApiErrors.NotFound();
        }

        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (version != subscription.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        var now = time.GetUtcNow();
        var expires = request.ExpirationDateTime ?? now + MaxLifetime;
        if (ExpiryError(expires, now) is { } error)
        {
            return ApiErrors.Validation("expirationDateTime", error);
        }

        if (request.ClientState is { Length: > ChangeSubscription.MaxClientState })
        {
            return ApiErrors.Validation("clientState", $"Up to {ChangeSubscription.MaxClientState} characters.");
        }

        subscription.ExpiresAt = expires;
        subscription.ClientState = request.ClientState ?? subscription.ClientState;
        await db.SaveChangesAsync(ct);
        ETags.Set(response, subscription.Version);
        return TypedResults.Ok(ChangeSubscriptionResponse.From(subscription));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, Caller caller, NotificationsDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        if (await FindAsync(db, caller, id, cancellationToken) is not { } subscription)
        {
            return TypedResults.NotFound();
        }

        var tenant = caller.TenantId;
        var subscriptionId = subscription.Id;
        var ct = cancellationToken;
        db.ChangeDeliveries.RemoveRange(await db.ChangeDeliveries.Where(d => d.TenantId == tenant && d.SubscriptionId == subscriptionId).ToListAsync(ct));
        db.ChangeSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static string? ExpiryError(DateTimeOffset expires, DateTimeOffset now) =>
        expires <= now ? "The expiry must be in the future."
        : expires > now + MaxLifetime + TimeSpan.FromMinutes(1) ? $"Subscriptions last up to {MaxLifetime.TotalDays} days; renew them before they expire."
        : null;

    /// <summary>The receiver proves it wants notifications: it must answer <c>POST {url}?validationToken=…</c> with 200 and the token as the body.</summary>
    private static async Task<string?> ValidateEndpointAsync(HttpClient client, string url, CancellationToken ct)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var validationUrl = url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "validationToken=" + token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, validationUrl);
            request.Headers.Add("X-PaperDotNet-Event", "validation");
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode && body.Trim() == token
                ? null
                : "The URL did not answer the validation request with the validation token.";
        }
        catch (HttpRequestException ex)
        {
            return $"The URL could not be reached: {ex.Message}";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "The URL did not answer in time.";
        }
    }
}

/// <summary>
/// Queues a delivery per matching change subscription whose owner can read the item (a Wolverine handler generated
/// ahead of time). Idempotent: one delivery per subscription and event.
/// </summary>
public static class ChangeSubscriber
{
    public static Task Handle(ItemAdded e, NotificationsDbContext db, IListItemStore items, TimeProvider time, CancellationToken cancellationToken) =>
        QueueAsync(e, "created", db, items, time, cancellationToken);

    public static Task Handle(ItemUpdated e, NotificationsDbContext db, IListItemStore items, TimeProvider time, CancellationToken cancellationToken) =>
        QueueAsync(e, "updated", db, items, time, cancellationToken);

    public static Task Handle(ItemDeleted e, NotificationsDbContext db, IListItemStore items, TimeProvider time, CancellationToken cancellationToken) =>
        QueueAsync(e, "deleted", db, items, time, cancellationToken);

    private static async Task QueueAsync(ItemEvent change, string type, NotificationsDbContext database, IListItemStore items, TimeProvider time, CancellationToken cancellationToken)
    {
        if (change.IsFolder)
        {
            return;
        }

        var db = database;
        var tenant = change.TenantId;
        var listId = change.ListId;
        var ct = cancellationToken;
        var now = time.GetUtcNow();
        // SQLite cannot compare DateTimeOffset values in SQL: the list's (few) subscriptions are filtered here.
        var subscriptions = (await db.ChangeSubscriptions.AsNoTracking().Where(s => s.TenantId == tenant && s.ListId == listId).ToListAsync(ct))
            .Where(s => (s.ItemId == null || s.ItemId == change.ItemId) && s.ExpiresAt > now && ChangeNotifications.TypesOf(s).Contains(type))
            .ToList();
        var readers = new Dictionary<Guid, bool>();
        foreach (var subscription in subscriptions)
        {
            var subscriptionId = subscription.Id;
            var eventId = change.EventId;
            if (await db.ChangeDeliveries.AnyAsync(d => d.TenantId == tenant && d.SubscriptionId == subscriptionId && d.EventId == eventId, ct))
            {
                continue;
            }

            if (!readers.TryGetValue(subscription.UserId, out var canRead))
            {
                var reader = items.ActingAs(new ChangeActor(tenant, subscription.UserId));
                readers[subscription.UserId] = canRead = type == "deleted"
                    ? await reader.GetListAsync(change.WorkspaceId, listId, ct) is not null
                    : await reader.GetAsync(change.WorkspaceId, listId, change.ItemId, ct) is not null;
            }

            if (canRead)
            {
                db.ChangeDeliveries.Add(new ChangeDelivery
                {
                    Id = Ids.New(),
                    TenantId = tenant,
                    SubscriptionId = subscriptionId,
                    EventId = eventId,
                    ChangeType = type,
                    WorkspaceId = change.WorkspaceId,
                    ListId = listId,
                    ItemId = change.ItemId,
                    OccurredAt = change.OccurredAt,
                    NextAttemptAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Posts pending change notifications, signed like user webhooks (<c>X-PaperDotNet-Signature</c> with the
/// subscription's secret). Failures are retried with backoff (1 min … 12 h, six attempts).
/// </summary>
internal sealed partial class ChangeDispatcher(
    NotificationsDbContext db, WebhookHttp http, IDataProtectionProvider protection, TimeProvider time, ILogger<ChangeDispatcher> logger) : ITenantRecurringJob
{
    public const string Name = "notifications.changes";
    public const string Schedule = "*/10 * * * * *";
    private const int BatchSize = 100;

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var pending = DeliveryStatuses.Pending;
        var ct = cancellationToken;
        var now = time.GetUtcNow();
        var due = (await context.ChangeDeliveries.Where(d => d.TenantId == tenant && d.Status == pending).ToListAsync(ct))
            .Where(d => d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(BatchSize)
            .ToList();
        if (due.Count == 0)
        {
            return;
        }

        var protector = protection.CreateProtector(WebhookPosts.SecretPurpose);
        var subscriptions = new Dictionary<Guid, ChangeSubscription?>();
        foreach (var delivery in due)
        {
            var subscriptionId = delivery.SubscriptionId;
            if (!subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                subscriptions[subscriptionId] = subscription =
                    await context.ChangeSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenant && s.Id == subscriptionId, ct);
            }

            if (subscription is null || subscription.ExpiresAt <= now)
            {
                delivery.Status = DeliveryStatuses.Failed;
                delivery.LastError = "The subscription expired or was deleted.";
                continue;
            }

            delivery.Attempts++;
            var body = JsonSerializer.Serialize(new ChangeNotificationBody(
            [
                new ChangeNotificationValue(subscription.Id, subscription.ClientState, delivery.ChangeType,
                    ChangeNotifications.Resource(delivery.WorkspaceId, delivery.ListId, delivery.ItemId),
                    new ChangeResourceData(delivery.ItemId, delivery.WorkspaceId, delivery.ListId),
                    delivery.OccurredAt, subscription.ExpiresAt, tenant),
            ]), NotificationsJson.Default.ChangeNotificationBody);
            var error = await WebhookPosts.PostAsync(http.Client, subscription.NotificationUrl, protector.Unprotect(subscription.Secret), "change", delivery.Id, body, time, ct);
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
                delivery.NextAttemptAt = time.GetUtcNow() + WebhookPosts.Backoff[delivery.Attempts - 1];
            }
        }

        await context.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Change notification {DeliveryId} failed: {Error}")]
    private partial void LogDeliveryFailed(Guid deliveryId, string error);
}

/// <summary>Removes subscriptions that expired a week ago and finished deliveries older than a week.</summary>
internal sealed class ChangeSubscriptionCleanupJob(NotificationsDbContext db, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "notifications.change-cleanup";
    public const string Schedule = "15 4 * * *";

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var pending = DeliveryStatuses.Pending;
        var ct = cancellationToken;
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(7);
        // SQLite cannot compare DateTimeOffset values in SQL: finished deliveries are filtered here.
        context.ChangeDeliveries.RemoveRange((await context.ChangeDeliveries.Where(d => d.TenantId == tenant && d.Status != pending).ToListAsync(ct))
            .Where(d => d.NextAttemptAt < cutoff));
        foreach (var subscription in (await context.ChangeSubscriptions.Where(s => s.TenantId == tenant).ToListAsync(ct)).Where(s => s.ExpiresAt < cutoff))
        {
            var subscriptionId = subscription.Id;
            context.ChangeDeliveries.RemoveRange(await context.ChangeDeliveries.Where(d => d.TenantId == tenant && d.SubscriptionId == subscriptionId).ToListAsync(ct));
            context.ChangeSubscriptions.Remove(subscription);
        }

        await context.SaveChangesAsync(ct);
    }
}
