using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Notifications.Data;
using PaperDotNet.Notifications.Features;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Notifications: inbox, settings, webhook channel, follows with digests (NTF-01…05), change subscriptions (API-06).</summary>
public sealed class NotificationTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestWebhookReceiver _hooks = new();
    private readonly TestHost _host;
    private HttpClient _admin = null!;
    private Guid _tenant;

    public NotificationTests() =>
        _host = new TestHost(services => services.AddKeyedSingleton<HttpMessageHandler>(WebhookDispatcher.HandlerKey, _hooks));

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        await using var scope = _host.Services.CreateAsyncScope();
        _tenant = (await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync("default", Ct))!.Id;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<Guid> MeAsync(HttpClient client) => Guid.Parse((await (await client.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id());

    private async Task<(Guid Id, HttpClient Client)> CreateUserAsync(string name)
    {
        using var created = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
        return (Guid.Parse((await created.JsonAsync(HttpStatusCode.Created)).Id()), await _host.SignInAsync(name, $"{name}-password-1"));
    }

    private async Task SendAsync(NotificationMessage message, params Guid[] users)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INotificationSender>().SendAsync(_tenant, message, users, Ct);
    }

    private async Task RunAsync<TJob>()
        where TJob : PaperDotNet.Jobs.Contracts.ITenantRecurringJob
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TJob>().RunAsync(_tenant, Ct);
    }

    private async Task<T> DbAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    private static async Task<List<JsonElement>> InboxAsync(HttpClient client, string query = "") =>
        [.. (await (await client.GetAsync($"/v1.0/me/notifications{query}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()];

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe)
        where T : struct
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task The_inbox_lists_marks_and_deletes_notifications()
    {
        var me = await MeAsync(_admin);
        await SendAsync(new NotificationMessage("system", "First"), me);
        await SendAsync(new NotificationMessage("system", "Second", DeduplicationKey: "once"), me);
        await SendAsync(new NotificationMessage("system", "Second again", DeduplicationKey: "once"), me);

        var inbox = await InboxAsync(_admin);
        Assert.Equal(["Second", "First"], inbox.Select(n => n.GetProperty("title").GetString()));
        Assert.Equal(2, (await (await _admin.GetAsync("/v1.0/me/notifications/unreadCount", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("count").GetInt32());

        var first = inbox[1].Id();
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"/v1.0/me/notifications/{first}/read", null, Ct)).StatusCode);
        Assert.Equal(["Second"], (await InboxAsync(_admin, "?unreadOnly=true")).Select(n => n.GetProperty("title").GetString()));
        await _admin.PostAsync("/v1.0/me/notifications/read", null, Ct);
        Assert.Empty(await InboxAsync(_admin, "?unreadOnly=true"));
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/me/notifications/{first}", Ct)).StatusCode);
        Assert.Single(await InboxAsync(_admin));

        // Nobody else sees them, not even with the id: another user, another tenant.
        var second = inbox[0].Id();
        var (_, bob) = await CreateUserAsync("bob");
        var foreign = await _host.CreateTenantAsync("other");
        foreach (var client in new[] { bob, foreign })
        {
            Assert.Empty(await InboxAsync(client));
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/v1.0/me/notifications/{second}/read", null, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/v1.0/me/notifications/{second}", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Webhooks_are_signed_retried_and_respect_quiet_hours()
    {
        var me = await MeAsync(_admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync("/v1.0/me/notificationSettings", new { webhookUrl = "http://hooks.example.test/pdn" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync("/v1.0/me/notificationSettings", new { webhookUrl = "https://user:pw@hooks.example.test/pdn" }, Ct)).StatusCode);

        var saved = await (await _admin.PutAsJsonAsync("/v1.0/me/notificationSettings", new
        {
            webhookUrl = "https://hooks.example.test/hook",
            channels = new Dictionary<string, object> { ["system"] = new { inApp = false, webhook = true } },
        }, Ct)).JsonAsync(HttpStatusCode.OK);
        var secret = saved.GetProperty("webhookSecret").GetString()!;
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);
        Assert.False((await (await _admin.GetAsync("/v1.0/me/notificationSettings", Ct)).JsonAsync(HttpStatusCode.OK)).TryGetProperty("webhookSecret", out _));

        Assert.Equal(HttpStatusCode.Accepted, (await _admin.PostAsync("/v1.0/me/notificationSettings/test", null, Ct)).StatusCode);
        await RunAsync<WebhookDispatcher>();
        var request = _hooks.Requests.Last(r => r.Url.AbsolutePath == "/hook");
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{request.Headers["X-PaperDotNet-Timestamp"]}.{request.Body}")));
        Assert.Equal(expected, request.Headers["X-PaperDotNet-Signature"]);
        Assert.Equal("Test notification", JsonElement.Parse(request.Body).GetProperty("notification").GetProperty("title").GetString());
        Assert.Empty(await InboxAsync(_admin)); // in-app is off for "system"

        // A failing receiver: the delivery is retried later.
        await _admin.PutAsJsonAsync("/v1.0/me/notificationSettings", new { webhookUrl = "https://fail.example.test/hook" }, Ct);
        await SendAsync(new NotificationMessage("reminder", "Retry me"), me);
        await RunAsync<WebhookDispatcher>();
        var delivery = await DbAsync(db => db.Deliveries.OrderByDescending(d => d.Id).FirstAsync(Ct));
        Assert.Equal(DeliveryStatuses.Pending, delivery.Status);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal("HTTP 500", delivery.LastError);
        Assert.True(delivery.NextAttemptAt > DateTimeOffset.UtcNow);

        // Quiet hours around now: the webhook waits until they end.
        var now = TimeOnly.FromDateTime(DateTime.UtcNow);
        await _admin.PutAsJsonAsync("/v1.0/me/notificationSettings", new
        {
            webhookUrl = "https://hooks.example.test/hook",
            quietHoursStart = now.AddHours(-1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            quietHoursEnd = now.AddHours(1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        }, Ct);
        await SendAsync(new NotificationMessage("reminder", "Later"), me);
        var later = await DbAsync(db => db.Deliveries.OrderByDescending(d => d.Id).FirstAsync(Ct));
        Assert.True(later.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(30));
    }

    [Fact]
    public async Task Followers_are_alerted_immediately_or_in_a_digest()
    {
        var (aliceId, alice) = await CreateUserAsync("alice");
        var ws = await Api.CreateWorkspaceAsync(_admin, "Team");
        await _admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = aliceId, role = "member" }, Ct);
        var list = (await Api.CreateListAsync(_admin, ws, "Plans")).Id();

        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/v1.0/me/subscriptions", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
        await _admin.PostAsJsonAsync("/v1.0/me/subscriptions", new { workspaceId = ws, listId = list }, Ct);
        var item = (await Api.CreateItemAsync(_admin, ws, list, new { title = "Roadmap" })).Id();

        var alert = await EventuallyAsync(async () => (await InboxAsync(alice)).Cast<JsonElement?>().FirstOrDefault(n => n!.Value.GetProperty("title").GetString() == "Roadmap was added"));
        Assert.Equal("itemChanged", alert.GetProperty("type").GetString());
        Assert.Equal(item, alert.GetProperty("itemId").GetString());
        Assert.Empty(await InboxAsync(_admin)); // no alerts for your own changes

        // Daily: changes wait for the digest.
        var subscription = (await (await alice.GetAsync("/v1.0/me/subscriptions", Ct)).JsonAsync(HttpStatusCode.OK))[0].Id();
        await alice.PostAsJsonAsync("/v1.0/me/subscriptions", new { workspaceId = ws, listId = list, frequency = "daily" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/v1.0/me/subscriptions", new { workspaceId = ws, listId = list, frequency = "hourly" }, Ct)).StatusCode);
        var url = $"{Api.Items(ws, list)}/{item}";
        var etag = (await _admin.GetAsync(url, Ct)).Headers.ETag!.Tag;
        (await _admin.SendAsync(Api.Patch(url, new { fields = new { title = "Roadmap 2027" } }, etag), Ct)).EnsureSuccessStatusCode();
        await EventuallyAsync(() => DbAsync(async db => await db.Digest.AnyAsync(d => d.UserId == aliceId, Ct) ? (bool?)true : null));
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DigestJob>().SendAsync(_tenant, aliceId, DateOnly.FromDateTime(DateTime.UtcNow), DateTimeOffset.UtcNow, Ct);
        }

        var digest = Assert.Single(await InboxAsync(alice), n => n.GetProperty("type").GetString() == "digest");
        Assert.Contains("Roadmap 2027 was updated", digest.GetProperty("body").GetString()!, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/v1.0/me/subscriptions/{subscription}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"/v1.0/me/subscriptions/{subscription}", Ct)).StatusCode);

        // Following needs read access.
        var (_, outsider) = await CreateUserAsync("outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync("/v1.0/me/subscriptions", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_notify_activity_notifies_people_once()
    {
        var (aliceId, alice) = await CreateUserAsync("alice");
        var ws = await Api.CreateWorkspaceAsync(_admin, "Ops");
        var list = (await Api.CreateListAsync(_admin, ws, "Tickets")).Id();
        using var workflow = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/workflows", new
        {
            name = "Tell Alice",
            definition = new
            {
                trigger = new { type = "itemAdded", list = "Tickets" },
                flow = new
                {
                    start = "tell",
                    nodes = new { tell = new { activity = "notify", inputs = new { to = new[] { "alice", "nobody" }, title = "New: {title}" } } },
                },
            },
        }, Ct);
        await workflow.JsonAsync(HttpStatusCode.Created);
        await Api.CreateItemAsync(_admin, ws, list, new { title = "Printer" });

        var notification = await EventuallyAsync(async () => (await InboxAsync(alice)).Cast<JsonElement?>().FirstOrDefault());
        Assert.Equal("New: Printer", notification.GetProperty("title").GetString());
        Assert.Equal("workflow", notification.GetProperty("type").GetString());
        Assert.Equal(list, notification.GetProperty("listId").GetString());
        _ = aliceId;
    }

    // ---- Change subscriptions (API-06) ---------------------------------------------------------

    private async Task<List<(Dictionary<string, string> Headers, string Body, JsonElement Change)>> ChangesAsync(string path, int count)
    {
        List<(Dictionary<string, string>, string, JsonElement)> Changes() => [.. _hooks.Requests
            .Where(r => r.Url.AbsolutePath == path && r.Headers.GetValueOrDefault("X-PaperDotNet-Event") == "change")
            .Select(r => (r.Headers, r.Body, JsonElement.Parse(r.Body).GetProperty("value")[0]))];
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Changes().Count < count && DateTime.UtcNow < deadline)
        {
            await RunAsync<ChangeDispatcher>();
            await Task.Delay(200, Ct);
        }

        return Changes();
    }

    [Fact]
    public async Task Change_subscribers_get_signed_notifications_for_changes_they_can_read()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Hooks");
        var list = (await Api.CreateListAsync(_admin, ws, "Entries", new[] { new { name = "note", type = "text" } })).Id();
        var resource = $"workspaces/{ws}/lists/{list}/items";
        using var created = await _admin.PostAsJsonAsync("/v1.0/changeSubscriptions", new
        {
            resource,
            changeTypes = new[] { "created", "deleted" },
            notificationUrl = "https://hooks.example.test/changes",
            clientState = "s3cr3t-state",
        }, Ct);
        var subscription = await created.JsonAsync(HttpStatusCode.Created);
        var secret = subscription.GetProperty("secret").GetString()!;
        var id = subscription.Id();
        Assert.Contains(_hooks.Requests, r => r.Url.AbsolutePath == "/changes" && r.Url.Query.Contains("validationToken=", StringComparison.Ordinal));
        Assert.False((await (await _admin.GetAsync($"/v1.0/changeSubscriptions/{id}", Ct)).JsonAsync(HttpStatusCode.OK)).TryGetProperty("secret", out _));

        var item = (await Api.CreateItemAsync(_admin, ws, list, new { title = "One" })).Id();
        var url = $"{Api.Items(ws, list)}/{item}";
        (await _admin.SendAsync(Api.Patch(url, new { fields = new { note = "x" } }, (await _admin.GetAsync(url, Ct)).Headers.ETag!.Tag), Ct)).EnsureSuccessStatusCode();

        var (headers, body, change) = Assert.Single(await ChangesAsync("/changes", 1));
        Assert.Equal("created", change.GetProperty("changeType").GetString());
        Assert.Equal("s3cr3t-state", change.GetProperty("clientState").GetString());
        Assert.Equal(id, change.GetProperty("subscriptionId").GetString());
        Assert.Equal(item, change.GetProperty("resourceData").GetProperty("id").GetString());
        Assert.Equal($"{resource}/{item}", change.GetProperty("resource").GetString());
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{headers["X-PaperDotNet-Timestamp"]}.{body}")));
        Assert.Equal(expected, headers["X-PaperDotNet-Signature"]);

        // Renewal needs the ETag; deleting stops notifications.
        var subscriptionUrl = $"/v1.0/changeSubscriptions/{id}";
        var current = await _admin.GetAsync(subscriptionUrl, Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await _admin.PatchAsJsonAsync(subscriptionUrl, new { expirationDateTime = DateTimeOffset.UtcNow.AddDays(2) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.SendAsync(Api.Patch(subscriptionUrl, new { expirationDateTime = DateTimeOffset.UtcNow.AddDays(2) }, current.Headers.ETag!.Tag), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync(subscriptionUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(subscriptionUrl, Ct)).StatusCode);
    }

    [Fact]
    public async Task Change_subscriptions_are_validated()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Hooks");
        var list = (await Api.CreateListAsync(_admin, ws, "Entries")).Id();
        var resource = $"workspaces/{ws}/lists/{list}/items";
        async Task<HttpStatusCode> CreateAsync(object body) => (await _admin.PostAsJsonAsync("/v1.0/changeSubscriptions", body, Ct)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { resource = "users", changeTypes = new[] { "created" }, notificationUrl = "https://hooks.example.test/v" }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { resource, changeTypes = new[] { "moved" }, notificationUrl = "https://hooks.example.test/v" }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { resource, changeTypes = new[] { "created" }, notificationUrl = "http://hooks.example.test/v" }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { resource, changeTypes = new[] { "created" }, notificationUrl = "https://hooks.example.test/v", expirationDateTime = DateTimeOffset.UtcNow.AddDays(40) }));

        // The receiver must echo the validation token.
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { resource, changeTypes = new[] { "created" }, notificationUrl = "https://fail.example.test/v" }));
        Assert.Equal(HttpStatusCode.NotFound, await CreateAsync(new { resource = $"workspaces/{ws}/lists/{Guid.NewGuid()}/items", changeTypes = new[] { "created" }, notificationUrl = "https://hooks.example.test/v" }));
    }

    [Fact]
    public async Task Change_subscribers_only_hear_about_items_they_can_read_and_other_tenants_see_nothing()
    {
        var (aliceId, alice) = await CreateUserAsync("alice");
        var ws = await Api.CreateWorkspaceAsync(_admin, "Hooks");
        await _admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = aliceId, role = "member" }, Ct);
        var list = (await Api.CreateListAsync(_admin, ws, "Entries")).Id();
        var resource = $"workspaces/{ws}/lists/{list}/items";
        using var folderResponse = await _admin.PostAsJsonAsync(Api.Items(ws, list), new { isFolder = true, fields = new { title = "Private" } }, Ct);
        var folder = (await folderResponse.JsonAsync(HttpStatusCode.Created)).Id();
        await _admin.PostAsJsonAsync($"{Api.Items(ws, list)}/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);

        using var created = await alice.PostAsJsonAsync("/v1.0/changeSubscriptions",
            new { resource, changeTypes = new[] { "created" }, notificationUrl = "https://hooks.example.test/access" }, Ct);
        var id = (await created.JsonAsync(HttpStatusCode.Created)).Id();

        await _admin.PostAsJsonAsync(Api.Items(ws, list), new { parentId = folder, fields = new { title = "Hidden" } }, Ct);
        var visible = (await Api.CreateItemAsync(_admin, ws, list, new { title = "Visible" })).Id();
        await ChangesAsync("/access", 1);
        await Task.Delay(1000, Ct);
        var changes = await ChangesAsync("/access", 1);
        Assert.Equal([visible], changes.Select(c => c.Change.GetProperty("resourceData").GetProperty("id").GetString()));

        // Subscriptions are per user and per tenant.
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/v1.0/changeSubscriptions/{id}", Ct)).StatusCode);
        var other = await _host.CreateTenantAsync("other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/changeSubscriptions/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/changeSubscriptions/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/v1.0/changeSubscriptions",
            new { resource, changeTypes = new[] { "created" }, notificationUrl = "https://hooks.example.test/intruder" }, Ct)).StatusCode);
    }
}

/// <summary>Receives webhook posts in tests: <c>fail.*</c> hosts answer 500; validation requests get their token back.</summary>
internal sealed class TestWebhookReceiver : HttpMessageHandler
{
    public ConcurrentQueue<(Uri Url, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue((request.RequestUri!, headers, body));
        var failing = request.RequestUri!.Host.StartsWith("fail.", StringComparison.Ordinal);
        var validationToken = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["validationToken"];
        if (validationToken is not null && !failing)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(validationToken) };
        }

        return new HttpResponseMessage(failing ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
    }
}
