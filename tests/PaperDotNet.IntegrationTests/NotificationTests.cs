using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Notifications.Data;
using PaperDotNet.Notifications.Features;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Notifications (phase 4c): inbox, settings, webhook channel, follows with digests, reminders (NTF-01…05).</summary>
public sealed class NotificationTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> MeAsync(HttpClient client) => (await (await client.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();

    private AsyncServiceScope Scope(TenantSummary tenant, Guid? userId = null) =>
        factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier, userId);

    private async Task RunAsync<TJob>(TenantSummary tenant)
        where TJob : class, PaperDotNet.Jobs.Contracts.ITenantRecurringJob
    {
        await using var scope = Scope(tenant);
        await scope.ServiceProvider.GetRequiredService<TJob>().RunAsync(Ct);
    }

    private async Task SendAsync(TenantSummary tenant, NotificationMessage message, params Guid[] users)
    {
        await using var scope = Scope(tenant);
        await scope.ServiceProvider.GetRequiredService<INotificationSender>().SendAsync(message, users, Ct);
    }

    private static async Task<List<JsonElement>> InboxAsync(HttpClient client, string query = "") =>
        (await (await client.GetAsync($"/v1.0/me/notifications{query}", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();

    [Fact]
    public async Task The_inbox_lists_marks_and_deletes_notifications()
    {
        var tenant = await factory.CreateTenantAsync("ntf-inbox");
        var other = await factory.CreateTenantAsync("ntf-inbox-b");
        var client = await ApiClient.CreateAsync(factory, "ntf-inbox");
        var foreign = await ApiClient.CreateAsync(factory, "ntf-inbox-b");
        var me = await MeAsync(client);

        await SendAsync(tenant, new NotificationMessage("system", "First"), me);
        await SendAsync(tenant, new NotificationMessage("system", "Second", DeduplicationKey: "once"), me);
        await SendAsync(tenant, new NotificationMessage("system", "Second again", DeduplicationKey: "once"), me);

        var inbox = await InboxAsync(client);
        Assert.Equal(["Second", "First"], inbox.Select(n => n.GetProperty("title").GetString()));
        Assert.Equal(2, (await (await client.GetAsync("/v1.0/me/notifications/unreadCount", Ct)).ReadJsonAsync()).GetProperty("count").GetInt32());

        var first = inbox[1].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/v1.0/me/notifications/{first}/read", null, Ct)).StatusCode);
        Assert.Equal(["Second"], (await InboxAsync(client, "?unreadOnly=true")).Select(n => n.GetProperty("title").GetString()));
        await client.PostAsync("/v1.0/me/notifications/read", null, Ct);
        Assert.Empty(await InboxAsync(client, "?unreadOnly=true"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/v1.0/me/notifications/{first}", Ct)).StatusCode);
        Assert.Single(await InboxAsync(client));

        // Nobody else sees them, not even with the id.
        var second = inbox[0].GetProperty("id").GetGuid();
        Assert.Empty(await InboxAsync(foreign));
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync($"/v1.0/me/notifications/{second}/read", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.DeleteAsync($"/v1.0/me/notifications/{second}", Ct)).StatusCode);
        _ = other;
    }

    [Fact]
    public async Task Webhooks_are_signed_retried_and_respect_quiet_hours()
    {
        var tenant = await factory.CreateTenantAsync("ntf-hook");
        var client = await ApiClient.CreateAsync(factory, "ntf-hook");
        var me = await MeAsync(client);

        var insecure = await client.PutAsJsonAsync("/v1.0/me/notificationSettings", new { webhookUrl = "http://hooks.example.test/pdn" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, insecure.StatusCode);
        var credentials = await client.PutAsJsonAsync("/v1.0/me/notificationSettings", new { webhookUrl = "https://user:pw@hooks.example.test/pdn" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, credentials.StatusCode);

        var saved = await (await client.PutAsJsonAsync("/v1.0/me/notificationSettings", new
        {
            webhookUrl = "https://hooks.example.test/ntf-hook",
            channels = new Dictionary<string, object> { ["system"] = new { inApp = false, webhook = true } },
        }, Ct)).ReadJsonAsync();
        var secret = saved.GetProperty("webhookSecret").GetString()!;
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);
        Assert.False((await (await client.GetAsync("/v1.0/me/notificationSettings", Ct)).ReadJsonAsync()).TryGetProperty("webhookSecret", out _));

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/v1.0/me/notificationSettings/test", null, Ct)).StatusCode);
        await RunAsync<WebhookDispatcher>(tenant);
        var request = TestWebhookReceiver.Instance.Requests.Last(r => r.Url.AbsolutePath == "/ntf-hook");
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{request.Headers["X-PaperDotNet-Timestamp"]}.{request.Body}")));
        Assert.Equal(expected, request.Headers["X-PaperDotNet-Signature"]);
        Assert.Equal("Test notification", JsonDocument.Parse(request.Body).RootElement.GetProperty("notification").GetProperty("title").GetString());
        Assert.Empty(await InboxAsync(client)); // in-app is off for "system"

        // A failing receiver: the delivery is retried later.
        await client.PutAsJsonAsync("/v1.0/me/notificationSettings", new { webhookUrl = "https://fail.example.test/ntf-hook" }, Ct);
        await SendAsync(tenant, new NotificationMessage("reminder", "Retry me"), me);
        await RunAsync<WebhookDispatcher>(tenant);
        await using (var scope = Scope(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var delivery = await db.Deliveries.OrderByDescending(d => d.Id).FirstAsync(Ct);
            Assert.Equal(DeliveryStatus.Pending, delivery.Status);
            Assert.Equal(1, delivery.Attempts);
            Assert.Equal("HTTP 500", delivery.LastError);
            Assert.True(delivery.NextAttemptAt > DateTimeOffset.UtcNow);
        }

        // Quiet hours around now: the webhook waits until they end.
        var now = TimeOnly.FromDateTime(DateTime.UtcNow);
        await client.PutAsJsonAsync("/v1.0/me/notificationSettings", new
        {
            webhookUrl = "https://hooks.example.test/ntf-hook",
            quietHoursStart = now.AddHours(-1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            quietHoursEnd = now.AddHours(1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        }, Ct);
        await SendAsync(tenant, new NotificationMessage("reminder", "Later"), me);
        await using (var scope = Scope(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var delivery = await db.Deliveries.OrderByDescending(d => d.Id).FirstAsync(Ct);
            Assert.True(delivery.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(30));
        }
    }

    [Fact]
    public async Task Followers_are_alerted_immediately_or_in_a_digest()
    {
        var tenant = await factory.CreateTenantAsync("ntf-follow");
        var admin = await ApiClient.CreateAsync(factory, "ntf-follow");
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "alice", password = "alice-password-1" }, Ct);
        var alice = await ApiClient.CreateAsync(factory, "ntf-follow", "alice", "alice-password-1");
        var aliceId = await MeAsync(alice);
        var ws = await admin.CreateWorkspaceAsync("Team");
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = aliceId, role = "member" }, Ct);
        var list = await admin.CreateListAsync(ws, "Plans");

        var follow = await alice.PostAsJsonAsync("/v1.0/me/subscriptions", new { workspaceId = ws, listId = list }, Ct);
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
        await admin.PostAsJsonAsync("/v1.0/me/subscriptions", new { workspaceId = ws, listId = list }, Ct);
        var item = (await admin.CreateItemAsync(ws, list, new { fields = new { title = "Roadmap" } })).GetProperty("id").GetGuid();

        await Eventually.WaitForAsync<bool>(async () => (await InboxAsync(alice)).Any(n => n.GetProperty("title").GetString() == "Roadmap was added") ? true : null);
        var alert = (await InboxAsync(alice)).First(n => n.GetProperty("type").GetString() == "itemChanged");
        Assert.Equal(item, alert.GetProperty("itemId").GetGuid());
        Assert.Empty(await InboxAsync(admin)); // no alerts for your own changes

        // Daily: changes wait for the digest.
        var subscription = (await (await alice.GetAsync("/v1.0/me/subscriptions", Ct)).ReadJsonAsync())[0].GetProperty("id").GetGuid();
        await alice.PostAsJsonAsync("/v1.0/me/subscriptions", new { workspaceId = ws, listId = list, frequency = "daily" }, Ct);
        var etag = (await admin.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}", Ct)).Headers.ETag!.Tag;
        await admin.SendWithEtagAsync(HttpMethod.Patch, $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}", etag, new { fields = new { title = "Roadmap 2027" } });
        await Eventually.WaitForAsync<bool>(async () =>
        {
            await using var scope = Scope(tenant);
            return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Digest.AnyAsync(d => d.UserId == aliceId, Ct) ? true : null;
        });
        await using (var scope = Scope(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<DigestJob>().SendAsync(aliceId, DateOnly.FromDateTime(DateTime.UtcNow), DateTimeOffset.UtcNow, Ct);
        }

        var digest = Assert.Single(await InboxAsync(alice), n => n.GetProperty("type").GetString() == "digest");
        Assert.Contains("Roadmap 2027 was updated", digest.GetProperty("body").GetString()!, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/v1.0/me/subscriptions/{subscription}", Ct)).StatusCode);

        // Following needs read access.
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "outsider", password = "outsider-password-1" }, Ct);
        var outsider = await ApiClient.CreateAsync(factory, "ntf-follow", "outsider", "outsider-password-1");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync("/v1.0/me/subscriptions", new { workspaceId = ws, listId = list }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Reminders_are_sent_once_for_due_tasks_and_upcoming_events()
    {
        var tenant = await factory.CreateTenantAsync("ntf-remind");
        var client = await ApiClient.CreateAsync(factory, "ntf-remind");
        var me = await MeAsync(client);
        var ws = await client.CreateWorkspaceAsync("Me");
        var tasks = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Tasks", templateKey = "tasks" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var calendar = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Calendar", templateKey = "calendar" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await client.CreateItemAsync(ws, tasks, new { fields = new { title = "Pay rent", dueDate = today, assignedTo = new[] { me } } });
        await client.CreateItemAsync(ws, tasks, new { fields = new { title = "Unassigned", dueDate = today } });
        var start = DateTimeOffset.UtcNow.AddMinutes(10);
        await client.CreateItemAsync(ws, calendar, new { fields = new { title = "Dentist", start, reminderMinutes = 15, attendees = new[] { me } } });
        await client.CreateItemAsync(ws, calendar, new { fields = new { title = "Far away", start = start.AddDays(3), reminderMinutes = 15 } });

        for (var i = 0; i < 2; i++)
        {
            await RunAsync<PaperDotNet.Tasks.Features.DueTaskReminderJob>(tenant);
            await RunAsync<PaperDotNet.Calendar.Features.EventReminderJob>(tenant);
        }

        var reminders = (await InboxAsync(client)).Where(n => n.GetProperty("type").GetString() == "reminder").Select(n => n.GetProperty("title").GetString()!).ToList();
        Assert.Equal(2, reminders.Count);
        Assert.Contains("Due today: Pay rent", reminders);
        Assert.Contains(reminders, r => r.StartsWith("Dentist starts", StringComparison.Ordinal));
    }
}
