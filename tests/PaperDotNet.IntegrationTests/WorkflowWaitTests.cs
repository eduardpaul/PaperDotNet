using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.IntegrationTests.Extension;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Waits of workflow runs (ADR-0036): approvals with escalation, delays, and bookmarks other code completes.</summary>
public sealed class WorkflowWaitTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private Guid _tenant;
    private string _workspace = "";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _tenant = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Finance");
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private string Workflows => $"/v1.0/workspaces/{_workspace}/workflows";

    private async Task<string> CreateWorkflowAsync(string name, string definition)
    {
        using var response = await _admin.PostAsJsonAsync(Workflows, new { name, definition = JsonNode.Parse(definition) }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private async Task<string> StartAsync(string workflow, string list, string item)
    {
        using var response = await _admin.PostAsJsonAsync($"{Workflows}/{workflow}/runs", new { listId = list, itemId = item }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Accepted)).Id();
    }

    private static async Task<JsonElement> WaitAsync(HttpClient client, string url, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var body = await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);
            if (condition(body))
            {
                return body;
            }

            Assert.True(DateTime.UtcNow < deadline, $"{url}: {body}");
            await Task.Delay(100, Ct);
        }
    }

    private Task<JsonElement> RunAsync(string run, string status) => WaitAsync(_admin, $"{Workflows}/runs/{run}", r => r.GetProperty("status").GetString() == status);

    private static List<JsonElement> Values(JsonElement page) => [.. page.GetProperty("value").EnumerateArray()];

    private async Task<string> CreateUserAsync(string name)
    {
        using var response = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
        var id = (await response.JsonAsync(HttpStatusCode.Created)).Id();
        using var member = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/members", new { userId = id, role = "member" }, Ct);
        Assert.True(member.IsSuccessStatusCode);
        return id;
    }

    private async Task TimersAsync(Func<WorkflowsDbContext, Task> change)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        await change(db);
        await db.SaveChangesAsync(Ct);
        await ActivatorUtilities.CreateInstance<WorkflowTimerJob>(scope.ServiceProvider).RunAsync(_tenant, Ct);
    }

    [Fact]
    public async Task Approvals_wait_for_a_decision_escalate_and_delays_resume_later()
    {
        await CreateUserAsync("alice");
        await CreateUserAsync("bob");
        var alice = await _host.SignInAsync("alice", "alice-password-1");
        var bob = await _host.SignInAsync("bob", "bob-password-1");
        var bills = (await Api.CreateListAsync(_admin, _workspace, "Bills", new object[] { new { name = "state", type = "text" } })).Id();
        var workflow = await CreateWorkflowAsync("Bill approval", """
            {
              "trigger": { "type": "manual", "list": "Bills" },
              "flow": { "start": "manager", "nodes": {
                "manager": { "activity": "approval", "inputs": { "assignees": ["alice"], "escalateTo": ["bob"], "title": "Approve {title}", "dueInHours": 48 },
                             "next": { "approved": "yes", "rejected": "no" } },
                "yes": { "activity": "item.update", "inputs": { "fields": { "state": "Approved by {step:manager.comment}" } } },
                "no": { "activity": "item.update", "inputs": { "fields": { "state": "Rejected" } } }
              } }
            }
            """);

        // Approved: alice decides, the run continues on the approved port.
        var first = (await Api.CreateItemAsync(_admin, _workspace, bills, new { title = "Bill 1" })).Id();
        var firstRun = await StartAsync(workflow, bills, first);
        await RunAsync(firstRun, "waiting");
        var approval = Assert.Single(Values(await WaitAsync(alice, "/v1.0/me/approvals", p => Values(p).Count == 1)));
        Assert.Equal("Approve Bill 1", approval.GetProperty("title").GetString());
        await WaitAsync(alice, "/v1.0/me/notifications", n => Values(n).Any(x => x.GetProperty("title").GetString() == "Approve Bill 1"));
        Assert.Empty(Values(await (await bob.GetAsync("/v1.0/me/approvals", Ct)).JsonAsync(HttpStatusCode.OK)));
        var decision = $"/v1.0/me/approvals/{approval.Id()}/decision";
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync(decision, new { outcome = "approved" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync(decision, new { outcome = "maybe" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync(decision, new { outcome = "approved", comment = "alice" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync(decision, new { outcome = "rejected" }, Ct)).StatusCode);
        Assert.Single(Values(await (await alice.GetAsync("/v1.0/me/approvals?status=approved", Ct)).JsonAsync(HttpStatusCode.OK)));
        Assert.Empty(Values(await (await alice.GetAsync("/v1.0/me/approvals?status=pending", Ct)).JsonAsync(HttpStatusCode.OK)));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/v1.0/me/approvals?status=maybe", Ct)).StatusCode);
        var completed = await RunAsync(firstRun, "completed");
        Assert.Equal("approved", completed.GetProperty("outputs").GetProperty("manager").GetProperty("outcome").GetString());
        Assert.Equal("Approved by alice", (await (await _admin.GetAsync($"{Api.Items(_workspace, bills)}/{first}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("fields").GetProperty("state").GetString());

        // Overdue: the minute job adds bob, who rejects.
        var second = (await Api.CreateItemAsync(_admin, _workspace, bills, new { title = "Bill 2" })).Id();
        var secondRun = await StartAsync(workflow, bills, second);
        await RunAsync(secondRun, "waiting");
        var runId = Guid.Parse(secondRun);
        await TimersAsync(async db =>
        {
            var pending = await db.Approvals.SingleAsync(a => a.TenantId == _tenant && a.RunId == runId, Ct);
            pending.DueAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        });
        var escalated = Assert.Single(Values(await WaitAsync(bob, "/v1.0/me/approvals", p => Values(p).Count == 1)));
        Assert.True(escalated.GetProperty("escalated").GetBoolean());
        await WaitAsync(bob, "/v1.0/me/notifications", n => Values(n).Any(x => x.GetProperty("title").GetString() == "Overdue: Approve Bill 2"));
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync($"/v1.0/me/approvals/{escalated.Id()}/decision", new { outcome = "rejected" }, Ct)).StatusCode);
        await RunAsync(secondRun, "completed");
        Assert.Equal("Rejected", (await (await _admin.GetAsync($"{Api.Items(_workspace, bills)}/{second}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("fields").GetProperty("state").GetString());

        // Cancelled runs cancel their approvals.
        var third = (await Api.CreateItemAsync(_admin, _workspace, bills, new { title = "Bill 3" })).Id();
        var thirdRun = await StartAsync(workflow, bills, third);
        await RunAsync(thirdRun, "waiting");
        using (var cancelled = await _admin.PostAsync($"{Workflows}/runs/{thirdRun}/cancel", null, Ct))
        {
            Assert.Equal("cancelled", (await cancelled.JsonAsync(HttpStatusCode.OK)).GetProperty("status").GetString());
        }

        Assert.Empty(Values(await (await alice.GetAsync("/v1.0/me/approvals?status=pending", Ct)).JsonAsync(HttpStatusCode.OK)));
        Assert.Single(Values(await (await alice.GetAsync("/v1.0/me/approvals?status=cancelled", Ct)).JsonAsync(HttpStatusCode.OK)));

        // Delays: the minute job resumes runs whose time has come.
        var later = await CreateWorkflowAsync("Later", """
            {
              "trigger": { "type": "manual" },
              "flow": { "start": "wait", "nodes": {
                "wait": { "activity": "delay", "inputs": { "hours": 24 }, "next": { "done": "note" } },
                "note": { "activity": "item.update", "inputs": { "fields": { "state": "a day later" } } }
              } }
            }
            """);
        var fourth = (await Api.CreateItemAsync(_admin, _workspace, bills, new { title = "Bill 4" })).Id();
        var laterRun = await StartAsync(later, bills, fourth);
        await RunAsync(laterRun, "waiting");
        await TimersAsync(_ => Task.CompletedTask);
        Assert.Equal("waiting", (await (await _admin.GetAsync($"{Workflows}/runs/{laterRun}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("status").GetString());
        var laterId = Guid.Parse(laterRun);
        await TimersAsync(async db =>
        {
            var delay = await db.Bookmarks.SingleAsync(b => b.TenantId == _tenant && b.RunId == laterId, Ct);
            Assert.Equal(BookmarkKinds.Delay, delay.Kind);
            delay.ResumeAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        });
        await RunAsync(laterRun, "completed");
        Assert.Equal("a day later", (await (await _admin.GetAsync($"{Api.Items(_workspace, bills)}/{fourth}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("fields").GetProperty("state").GetString());

        // Approvals belong to their tenant.
        var other = await _host.CreateTenantAsync("waits-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(decision, new { outcome = "approved" }, Ct)).StatusCode);
        Assert.Empty(Values(await (await other.GetAsync("/v1.0/me/approvals", Ct)).JsonAsync(HttpStatusCode.OK)));
    }

    [Fact]
    public async Task Activities_wait_until_other_code_completes_them_or_time_out()
    {
        Assert.True((await _admin.PostAsync("/v1.0/extensions/tests.tickets/enable", null, Ct)).IsSuccessStatusCode);
        var tickets = (await Api.CreateListAsync(_admin, _workspace, "Tickets", new object[] { new { name = "note", type = "text" } })).Id();
        string Item(string id) => $"{Api.Items(_workspace, tickets)}/{id}";
        var workflow = await CreateWorkflowAsync("Wait for a signal", """
            {
              "trigger": { "type": "manual", "list": "Tickets" },
              "flow": { "start": "wait", "nodes": {
                "wait": { "activity": "tests.tickets.await", "inputs": { "days": 14 }, "next": { "signalled": "yes", "timeout": "no" } },
                "yes": { "activity": "item.update", "inputs": { "fields": { "note": "signalled by {step:wait.by}" } } },
                "no": { "activity": "item.update", "inputs": { "fields": { "note": "timed out" } } }
              } }
            }
            """);

        async Task<(string Item, string Run)> StartTicketAsync()
        {
            var ticket = (await Api.CreateItemAsync(_admin, _workspace, tickets, new { title = "Ticket" })).Id();
            return (ticket, await StartAsync(workflow, tickets, ticket));
        }

        async Task<string?> NoteAsync(string id) =>
            (await (await _admin.GetAsync(Item(id), Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("fields").TryGetProperty("note", out var note) ? note.GetString() : null;

        await using var scope = _host.Services.CreateAsyncScope();
        var bookmarks = scope.ServiceProvider.GetRequiredService<IWorkflowBookmarks>();
        static string Key(string id) => Guid.Parse(id).ToString("N");

        // The run waits until other code completes the wait; its payload is the node's output and picks the port.
        var (signalled, signalledRun) = await StartTicketAsync();
        await RunAsync(signalledRun, "waiting");
        var open = Assert.Single(await bookmarks.ListOpenAsync(_tenant, AwaitSignalActivity.WaitKind, Guid.Parse(_workspace), 0, 10, Ct));
        Assert.Equal(Key(signalled), open.Key);
        Assert.Equal(signalled, open.Data!["about"]!.GetValue<string>());
        Assert.True(open.ResumeAt > DateTimeOffset.UtcNow.AddDays(13));
        Assert.True(await bookmarks.CompleteAsync(_tenant, AwaitSignalActivity.WaitKind, Key(signalled), new JsonObject { ["outcome"] = "signalled", ["by"] = "test" }, Ct));
        var done = await RunAsync(signalledRun, "completed");
        Assert.Equal("signalled", done.GetProperty("outputs").GetProperty("wait").GetProperty("outcome").GetString());
        Assert.Equal("signalled by test", await NoteAsync(signalled));

        // Completing again changes nothing; the engine's own waits cannot be completed from outside; tenants are apart.
        Assert.False(await bookmarks.CompleteAsync(_tenant, AwaitSignalActivity.WaitKind, Key(signalled), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => bookmarks.CompleteAsync(_tenant, "approval", "x", null, Ct));
        Assert.Empty(await bookmarks.ListOpenAsync(Guid.NewGuid(), AwaitSignalActivity.WaitKind, Guid.Parse(_workspace), 0, 10, Ct));

        // A completion that arrives before the run waits is kept, and the run continues right away when it gets there.
        var early = (await Api.CreateItemAsync(_admin, _workspace, tickets, new { title = "Ticket" })).Id();
        Assert.True(await bookmarks.CompleteAsync(_tenant, AwaitSignalActivity.WaitKind, Key(early), new JsonObject { ["outcome"] = "signalled", ["by"] = "early" }, Ct));
        await RunAsync(await StartAsync(workflow, tickets, early), "completed");
        Assert.Equal("signalled by early", await NoteAsync(early));

        // Without a signal the wait times out (the minute job) and the run continues on the timeout port.
        var (silent, silentRun) = await StartTicketAsync();
        await RunAsync(silentRun, "waiting");
        var silentId = Guid.Parse(silentRun);
        await TimersAsync(async db =>
        {
            var wait = await db.Bookmarks.SingleAsync(b => b.TenantId == _tenant && b.RunId == silentId, Ct);
            wait.ResumeAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        });
        await RunAsync(silentRun, "completed");
        Assert.Equal("timed out", await NoteAsync(silent));
    }
}
