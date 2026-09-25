using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Data;

namespace PaperDotNet.Notifications.Features;

/// <summary>
/// Create body. <c>resource</c> is <c>workspaces/{id}/lists/{id}/items</c> (the whole list) or
/// <c>…/items/{id}</c> (one item); <c>changeTypes</c> any of <c>created</c>, <c>updated</c>,
/// <c>deleted</c>; <c>expirationDateTime</c> at most 30 days ahead (default: the maximum).
/// </summary>
public sealed record ChangeSubscriptionRequest(
    string? Resource, IReadOnlyList<string>? ChangeTypes, string? NotificationUrl, string? ClientState, DateTimeOffset? ExpirationDateTime);

/// <summary>Renewal body: a new expiry and optionally a new client state.</summary>
public sealed record ChangeSubscriptionUpdate(DateTimeOffset? ExpirationDateTime, string? ClientState);

/// <summary>A change subscription; <see cref="Secret"/> is only returned when it is created (shown once).</summary>
public sealed record ChangeSubscriptionResponse(
    Guid Id, string Resource, IReadOnlyList<string> ChangeTypes, string NotificationUrl, string? ClientState,
    DateTimeOffset ExpirationDateTime, DateTimeOffset CreatedAt, string? Secret = null)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }


    internal static ChangeSubscriptionResponse From(ChangeSubscription s) =>
        new(s.Id, ChangeNotifications.Resource(s.WorkspaceId, s.ListId, s.ItemId), s.ChangeTypes, s.NotificationUrl, s.ClientState, s.ExpiresAt, s.CreatedAt) { ETag = ETags.From(s.Version) };
}

/// <summary>
/// Change notifications (API-06): API clients subscribe to changes of a list or item and receive
/// signed POSTs (as for user webhooks). The URL must answer a validation request first. Notifications
/// carry ids only, and only for items the subscription's owner can read.
/// </summary>
internal static partial class ChangeNotifications
{
    public const int MaxPerUser = 100;
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);
    public static readonly string[] Types = ["created", "updated", "deleted"];

    public static string Resource(Guid workspaceId, Guid listId, Guid? itemId) =>
        $"workspaces/{workspaceId}/lists/{listId}/items" + (itemId is { } id ? $"/{id}" : "");

    [GeneratedRegex(@"^/?workspaces/(?<ws>[0-9a-fA-F-]{36})/lists/(?<list>[0-9a-fA-F-]{36})/items(/(?<item>[0-9a-fA-F-]{36}))?$")]
    private static partial Regex ResourcePattern();

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("changeSubscriptions", "Change notifications");
        group.MapGet("", ListAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("ListChangeSubscriptions");
        group.MapPost("", CreateAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("CreateChangeSubscription");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("GetChangeSubscription");
        group.MapPatch("/{id:guid}", RenewAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("RenewChangeSubscription");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireScope(NotificationScopes.ChangeSubscriptions).WithName("DeleteChangeSubscription");
    }

    /// <summary>The caller's subscriptions (including expired ones until they are cleaned up).</summary>
    private static async Task<Ok<Page<ChangeSubscriptionResponse>>> ListAsync(NotificationsDbContext db, ICurrentUser user, HttpRequest http, CancellationToken ct)
    {
        var page = PageRequest.From(http);
        var query = db.ChangeSubscriptions.AsNoTracking().Where(s => s.UserId == user.UserId);
        if (page.After is { } after)
        {
            query = query.Where(s => s.Id.CompareTo(after) < 0);
        }

        var subscriptions = await query.OrderByDescending(s => s.Id).Take(page.Top + 1).ToListAsync(ct);
        return TypedResults.Ok(Page.Create(subscriptions.Select(ChangeSubscriptionResponse.From).ToList(), page, http, s => s.Id));
    }

    private static async Task<Results<Ok<ChangeSubscriptionResponse>, ProblemHttpResult>> GetAsync(
        Guid id, NotificationsDbContext db, ICurrentUser user, HttpResponse response, CancellationToken ct)
    {
        var subscription = await db.ChangeSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == user.UserId, ct);
        if (subscription is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, subscription.Version);
        return TypedResults.Ok(ChangeSubscriptionResponse.From(subscription));
    }

    private static async Task<Results<Created<ChangeSubscriptionResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        ChangeSubscriptionRequest request, NotificationsDbContext db, ICurrentUser user, IListItemStore items, WebhookHttp http,
        IDataProtectionProvider protection, IOptions<NotificationsOptions> options, TimeProvider time, HttpResponse response, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "userRequired", "Change subscriptions belong to users.");
        }

        var errors = new Dictionary<string, string[]>();
        var match = ResourcePattern().Match(request.Resource ?? "");
        Guid workspaceId = default, listId = default;
        Guid? itemId = null;
        if (!match.Success || !Guid.TryParse(match.Groups["ws"].Value, out workspaceId) || !Guid.TryParse(match.Groups["list"].Value, out listId)
            || (match.Groups["item"].Success && !Guid.TryParse(match.Groups["item"].Value, out _)))
        {
            errors["resource"] = ["Use workspaces/{id}/lists/{id}/items or workspaces/{id}/lists/{id}/items/{id}."];
        }
        else if (match.Groups["item"].Success)
        {
            itemId = Guid.Parse(match.Groups["item"].Value);
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
            ? await items.GetAsync(workspaceId, listId, item, ct) is not null
            : await items.GetListAsync(workspaceId, listId, ct) is not null;
        if (!visible)
        {
            return ApiErrors.NotFound("The resource was not found.");
        }

        if (await db.ChangeSubscriptions.CountAsync(s => s.UserId == userId, ct) >= MaxPerUser)
        {
            return ApiErrors.Conflict("tooManySubscriptions", $"You can have up to {MaxPerUser} change subscriptions.");
        }

        if (await ValidateEndpointAsync(http.Client, request.NotificationUrl!, ct) is { } validationError)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["notificationUrl"] = [validationError] });
        }

        var secret = "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var subscription = new ChangeSubscription
        {
            Id = Ids.New(),
            UserId = userId,
            WorkspaceId = workspaceId,
            ListId = listId,
            ItemId = itemId,
            ChangeTypes = types,
            NotificationUrl = request.NotificationUrl!,
            ClientState = request.ClientState,
            Secret = protection.CreateProtector(WebhookDispatcher.SecretPurpose).Protect(secret),
            ExpiresAt = expires,
        };
        db.ChangeSubscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, subscription.Version);
        return TypedResults.Created($"/v1.0/changeSubscriptions/{subscription.Id}", ChangeSubscriptionResponse.From(subscription) with { Secret = secret });
    }

    /// <summary>Extends (or shortens) the subscription; needs <c>If-Match</c>.</summary>
    private static async Task<Results<Ok<ChangeSubscriptionResponse>, ValidationProblem, ProblemHttpResult>> RenewAsync(
        Guid id, ChangeSubscriptionUpdate request, NotificationsDbContext db, ICurrentUser user, TimeProvider time,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        var subscription = await db.ChangeSubscriptions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == user.UserId, ct);
        if (subscription is null)
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
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["expirationDateTime"] = [error] });
        }

        if (request.ClientState is { Length: > ChangeSubscription.MaxClientState })
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["clientState"] = [$"Up to {ChangeSubscription.MaxClientState} characters."] });
        }

        subscription.ExpiresAt = expires;
        subscription.ClientState = request.ClientState ?? subscription.ClientState;
        await db.SaveChangesAsync(ct);
        ETags.Set(response, subscription.Version);
        return TypedResults.Ok(ChangeSubscriptionResponse.From(subscription));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, NotificationsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (!await db.ChangeSubscriptions.AnyAsync(s => s.Id == id && s.UserId == user.UserId, ct))
        {
            return TypedResults.NotFound();
        }

        await db.ChangeDeliveries.Where(d => d.SubscriptionId == id).ExecuteDeleteAsync(ct);
        await db.ChangeSubscriptions.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static string? ExpiryError(DateTimeOffset expires, DateTimeOffset now) =>
        expires <= now ? "The expiry must be in the future."
        : expires > now + MaxLifetime + TimeSpan.FromMinutes(1) ? $"Subscriptions last up to {MaxLifetime.TotalDays} days; renew them before they expire."
        : null;

    /// <summary>
    /// The receiver proves it wants notifications: it must answer <c>POST {url}?validationToken=…</c>
    /// with 200 and the token as the body.
    /// </summary>
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

/// <summary>Queues a delivery per matching subscription whose owner can read the item (idempotent per event).</summary>
internal sealed class ChangeNotifier(NotificationsDbContext db, ITenantScopeFactory scopes, TimeProvider time)
    : IEventSubscriber<ItemAdded>, IEventSubscriber<ItemUpdated>, IEventSubscriber<ItemDeleted>
{
    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken) => QueueAsync(integrationEvent, "created", cancellationToken);

    public Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken) => QueueAsync(integrationEvent, "updated", cancellationToken);

    public Task HandleAsync(ItemDeleted integrationEvent, CancellationToken cancellationToken) => QueueAsync(integrationEvent, "deleted", cancellationToken);

    private async Task QueueAsync(ItemEvent change, string type, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var subscriptions = (await db.ChangeSubscriptions.AsNoTracking()
                .Where(s => s.ListId == change.ListId && (s.ItemId == null || s.ItemId == change.ItemId) && s.ExpiresAt > now)
                .ToListAsync(ct))
            .Where(s => s.ChangeTypes.Contains(type))
            .ToList();
        if (subscriptions.Count == 0)
        {
            return;
        }

        var ids = subscriptions.Select(s => s.Id).ToList();
        var done = await db.ChangeDeliveries.Where(d => d.EventId == change.EventId && ids.Contains(d.SubscriptionId)).Select(d => d.SubscriptionId).ToListAsync(ct);
        var readers = new Dictionary<Guid, bool>();
        foreach (var subscription in subscriptions.Where(s => !done.Contains(s.Id)))
        {
            if (!readers.TryGetValue(subscription.UserId, out var canRead))
            {
                readers[subscription.UserId] = canRead = await CanReadAsync(change, subscription.UserId, type, ct);
            }

            if (canRead)
            {
                db.ChangeDeliveries.Add(new ChangeDelivery
                {
                    Id = Ids.New(),
                    SubscriptionId = subscription.Id,
                    EventId = change.EventId,
                    ChangeType = type,
                    WorkspaceId = change.WorkspaceId,
                    ListId = change.ListId,
                    ItemId = change.ItemId,
                    OccurredAt = change.OccurredAt,
                    NextAttemptAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Whether the owner can read the item now (for deletions: the list).</summary>
    private async Task<bool> CanReadAsync(ItemEvent change, Guid userId, string type, CancellationToken ct)
    {
        await using var scope = scopes.CreateScope(change.TenantId, change.TenantIdentifier, userId);
        var store = scope.ServiceProvider.GetRequiredService<IListItemStore>();
        return type == "deleted"
            ? await store.GetListAsync(change.WorkspaceId, change.ListId, ct) is not null
            : await store.GetAsync(change.WorkspaceId, change.ListId, change.ItemId, ct) is not null;
    }
}

/// <summary>
/// Posts pending change notifications, signed like user webhooks (<c>X-PaperDotNet-Signature</c>
/// with the subscription's secret). Failures are retried with backoff (1 min … 12 h, six attempts).
/// </summary>
internal sealed partial class ChangeDispatcher(
    NotificationsDbContext db, WebhookHttp http, IDataProtectionProvider protection, TimeProvider time,
    ITenantContext tenant, ILogger<ChangeDispatcher> logger) : ITenantRecurringJob
{
    public const string Name = "notifications.changes";
    public const string Schedule = "*/10 * * * * *";
    private const int BatchSize = 100;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = await db.ChangeDeliveries.Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).Take(BatchSize).ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return;
        }

        var ids = due.Select(d => d.SubscriptionId).Distinct().ToList();
        var subscriptions = await db.ChangeSubscriptions.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        var protector = protection.CreateProtector(WebhookDispatcher.SecretPurpose);
        foreach (var delivery in due)
        {
            if (!subscriptions.TryGetValue(delivery.SubscriptionId, out var subscription) || subscription.ExpiresAt <= now)
            {
                delivery.Status = DeliveryStatus.Failed;
                delivery.LastError = "The subscription expired or was deleted.";
                continue;
            }

            delivery.Attempts++;
            var error = await PostAsync(subscription, protector.Unprotect(subscription.Secret), delivery, cancellationToken);
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
                    delivery.NextAttemptAt = time.GetUtcNow() + Backoff[delivery.Attempts - 1];
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string?> PostAsync(ChangeSubscription subscription, string secret, ChangeDelivery delivery, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    subscriptionId = subscription.Id,
                    clientState = subscription.ClientState,
                    changeType = delivery.ChangeType,
                    resource = ChangeNotifications.Resource(delivery.WorkspaceId, delivery.ListId, delivery.ItemId),
                    resourceData = new { id = delivery.ItemId, workspaceId = delivery.WorkspaceId, listId = delivery.ListId },
                    occurredAt = delivery.OccurredAt,
                    subscriptionExpirationDateTime = subscription.ExpiresAt,
                    tenant = tenant.TenantIdentifier,
                },
            },
        }, Json);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.NotificationUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-PaperDotNet-Event", "change");
        request.Headers.Add("X-PaperDotNet-Delivery", delivery.Id.ToString());
        request.Headers.Add("X-PaperDotNet-Timestamp", timestamp);
        request.Headers.Add("X-PaperDotNet-Signature", WebhookDispatcher.Sign(secret, timestamp, body));
        try
        {
            using var response = await http.Client.SendAsync(request, ct);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Change notification {DeliveryId} failed: {Error}")]
    private partial void LogDeliveryFailed(Guid deliveryId, string error);
}

/// <summary>Removes subscriptions that expired a week ago and finished deliveries older than a week.</summary>
internal sealed class ChangeSubscriptionCleanupJob(NotificationsDbContext db, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "notifications.change-cleanup";
    public const string Schedule = "15 4 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(7);
        await db.ChangeDeliveries.Where(d => d.Status != DeliveryStatus.Pending && d.NextAttemptAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        var expired = db.ChangeSubscriptions.Where(s => s.ExpiresAt < cutoff).Select(s => s.Id);
        await db.ChangeDeliveries.Where(d => expired.Contains(d.SubscriptionId)).ExecuteDeleteAsync(cancellationToken);
        await db.ChangeSubscriptions.Where(s => s.ExpiresAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
