using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Reminders of due tasks and upcoming events (NTF-02), sent once.</summary>
public sealed class ReminderTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task RunAsync<TJob>()
        where TJob : PaperDotNet.Jobs.Contracts.ITenantRecurringJob
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var tenant = (await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync("default", Ct))!.Id;
        await scope.ServiceProvider.GetRequiredService<TJob>().RunAsync(tenant, Ct);
    }

    private async Task<string> ListAsync(string ws, string name, string templateKey)
    {
        using var response = await _client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    [Fact]
    public async Task Reminders_are_sent_once_for_due_tasks_and_upcoming_events()
    {
        var me = (await (await _client.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id();
        var ws = await Api.CreateWorkspaceAsync(_client, "Me");
        var tasks = await ListAsync(ws, "Tasks", "tasks");
        var calendar = await ListAsync(ws, "Calendar", "calendar");
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await Api.CreateItemAsync(_client, ws, tasks, new { title = "Pay rent", dueDate = today, assignedTo = new[] { me } });
        await Api.CreateItemAsync(_client, ws, tasks, new { title = "Unassigned", dueDate = today });
        var start = DateTimeOffset.UtcNow.AddMinutes(10);
        await Api.CreateItemAsync(_client, ws, calendar, new { title = "Dentist", start, reminderMinutes = 15, attendees = new[] { me } });
        await Api.CreateItemAsync(_client, ws, calendar, new { title = "Far away", start = start.AddDays(3), reminderMinutes = 15 });

        for (var i = 0; i < 2; i++)
        {
            await RunAsync<PaperDotNet.Tasks.Features.DueTaskReminderJob>();
            await RunAsync<PaperDotNet.Calendar.Features.EventReminderJob>();
        }

        var inbox = (await (await _client.GetAsync("/v1.0/me/notifications", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray();
        var reminders = inbox.Where(n => n.GetProperty("type").GetString() == "reminder").Select(n => n.GetProperty("title").GetString()!).ToList();
        Assert.Equal(2, reminders.Count);
        Assert.Contains("Due today: Pay rent", reminders);
        Assert.Contains(reminders, r => r.StartsWith("Dentist starts", StringComparison.Ordinal));
    }
}
