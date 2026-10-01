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

/// <summary>Reminders of Tasks and Calendar (NTF-02), ported with T11; the rest of the notification tests are in ../NotificationTests.cs.</summary>
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
