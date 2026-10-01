using System.Globalization;
using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Tasks: checklists, links, cross-list views, recurrence, tasks from documents (TSK-01…06).</summary>
public sealed class TaskTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<string> ListAsync(HttpClient client, string ws, string name, string templateKey)
    {
        using var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static async Task<string> TaskAsync(HttpClient client, string ws, string list, object fields) => (await Api.CreateItemAsync(client, ws, list, fields)).Id();

    private static string Item(string ws, string list, string item) => $"{Api.Items(ws, list)}/{item}";

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<string> MeAsync(HttpClient client) => (await (await client.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id();

    private static async Task<JsonElement> GetAsync(HttpClient client, string url) => await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);

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
    public async Task Checklists_and_links_keep_tasks_consistent()
    {
        var ws = await Api.CreateWorkspaceAsync(_client, "Project");
        var list = await ListAsync(_client, ws, "Tasks", "tasks");
        var a = await TaskAsync(_client, ws, list, new { title = "Release" });
        var b = await TaskAsync(_client, ws, list, new { title = "Write notes" });
        var c = await TaskAsync(_client, ws, list, new { title = "Test" });

        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync($"{Item(ws, list, a)}/checklist", new[] { new { text = "Tag", done = true }, new { text = "Publish", done = false } }, Ct)).StatusCode);
        var entries = (await GetAsync(_client, $"{Item(ws, list, a)}/checklist")).GetProperty("value");
        Assert.Equal(["Tag", "Publish"], entries.EnumerateArray().Select(e => e.GetProperty("text").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"{Item(ws, list, a)}/checklist", new[] { new { text = " ", done = false } }, Ct)).StatusCode);

        Task<HttpResponseMessage> LinkAsync(string from, string kind, string to) =>
            _client.PostAsJsonAsync($"{Item(ws, list, from)}/links", new { kind, workspaceId = ws, listId = list, itemId = to }, Ct);

        async Task<string?> CodeAsync(Task<HttpResponseMessage> request) => (await (await request).JsonAsync(HttpStatusCode.Conflict)).GetProperty("code").GetString();

        Assert.Equal(HttpStatusCode.Created, (await LinkAsync(a, "subtask", b)).StatusCode);
        Assert.Equal("cycle", await CodeAsync(LinkAsync(b, "subtask", a)));
        Assert.Equal("hasParent", await CodeAsync(LinkAsync(c, "subtask", b)));
        Assert.Equal(HttpStatusCode.Created, (await LinkAsync(a, "blockedBy", c)).StatusCode);
        Assert.Equal("cycle", await CodeAsync(LinkAsync(c, "blockedBy", a)));
        Assert.Equal("linkExists", await CodeAsync(LinkAsync(a, "blockedBy", c)));
        Assert.Equal(HttpStatusCode.BadRequest, (await LinkAsync(a, "document", c)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LinkAsync(a, "nope", c)).StatusCode);

        Assert.Equal(a, (await GetAsync(_client, $"{Item(ws, list, b)}/links")).GetProperty("parent").GetProperty("itemId").GetString());
        var blocking = Assert.Single((await GetAsync(_client, $"{Item(ws, list, c)}/links")).GetProperty("blocking").EnumerateArray());
        Assert.Equal("Release", blocking.GetProperty("title").GetString());

        var linkId = blocking.GetProperty("linkId").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{Item(ws, list, c)}/links/{linkId}", Ct)).StatusCode);
        Assert.Equal(0, (await GetAsync(_client, $"{Item(ws, list, a)}/links")).GetProperty("blockedBy").GetArrayLength());

        // Only task lists have task features.
        var notes = await ListAsync(_client, ws, "Notes", "notes");
        var note = await TaskAsync(_client, ws, notes, new { title = "Idea" });
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Item(ws, notes, note)}/checklist", Ct)).StatusCode);
    }

    [Fact]
    public async Task My_tasks_span_every_task_list()
    {
        var me = await MeAsync(_client);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var ws1 = await Api.CreateWorkspaceAsync(_client, "Home");
        var ws2 = await Api.CreateWorkspaceAsync(_client, "Work");
        var home = await ListAsync(_client, ws1, "Tasks", "tasks");
        var work = await ListAsync(_client, ws2, "Sprint", "tasks");
        await TaskAsync(_client, ws1, home, new { title = "Due today", dueDate = Day(today), assignedTo = new[] { me } });
        await TaskAsync(_client, ws2, work, new { title = "Late", dueDate = Day(today.AddDays(-2)), assignedTo = new[] { me } });
        await TaskAsync(_client, ws2, work, new { title = "Later", dueDate = Day(today.AddDays(40)), assignedTo = new[] { me } });
        await TaskAsync(_client, ws2, work, new { title = "Done", dueDate = Day(today), status = "completed", assignedTo = new[] { me } });
        await TaskAsync(_client, ws1, home, new { title = "Someone's", dueDate = Day(today) });

        async Task<List<string>> ViewAsync(string view) =>
            [.. (await GetAsync(_client, $"/v1.0/me/tasks?view={view}")).GetProperty("value").EnumerateArray().Select(t => t.GetProperty("title").GetString()!)];

        Assert.Equal(["Late", "Due today", "Later"], await ViewAsync("mine"));
        Assert.Equal(["Late"], await ViewAsync("overdue"));
        Assert.Equal(["Due today"], await ViewAsync("dueThisWeek"));
        Assert.Equal(4, (await ViewAsync("all")).Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/v1.0/me/tasks?view=nope", Ct)).StatusCode);
        Assert.Equal("Sprint", (await GetAsync(_client, "/v1.0/me/tasks")).GetProperty("value")[0].GetProperty("listName").GetString());
    }

    [Fact]
    public async Task Completing_a_repeating_task_creates_the_next_one()
    {
        var ws = await Api.CreateWorkspaceAsync(_client, "Chores");
        var list = await ListAsync(_client, ws, "Tasks", "tasks");
        var noDue = await TaskAsync(_client, ws, list, new { title = "Someday" });
        var task = await TaskAsync(_client, ws, list, new { title = "Water plants", startDate = "2026-10-01", dueDate = "2026-10-05", priority = "high" });
        await _client.PutAsJsonAsync($"{Item(ws, list, task)}/checklist", new[] { new { text = "Balcony", done = true } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"{Item(ws, list, task)}/recurrence", new { rule = "FREQ=SOMETIMES" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"{Item(ws, list, noDue)}/recurrence", new { rule = "FREQ=DAILY" }, Ct)).StatusCode);
        var set = await (await _client.PutAsJsonAsync($"{Item(ws, list, task)}/recurrence", new { rule = "RRULE:FREQ=WEEKLY;BYDAY=MO" }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("2026-10-12", set.GetProperty("nextDueDate").GetString());

        var etag = (await _client.GetAsync(Item(ws, list, task), Ct)).Headers.ETag!.Tag;
        (await _client.SendAsync(Api.Patch(Item(ws, list, task), new { fields = new { status = "completed" } }, etag), Ct)).EnsureSuccessStatusCode();

        var next = await EventuallyAsync(async () =>
        {
            var items = (await GetAsync(_client, $"{Api.Items(ws, list)}?$filter=fields/dueDate eq 2026-10-12")).GetProperty("value");
            return items.GetArrayLength() == 0 ? (JsonElement?)null : items[0];
        });
        var fields = next.GetProperty("fields");
        Assert.Equal("Water plants", fields.GetProperty("title").GetString());
        Assert.Equal("notStarted", fields.GetProperty("status").GetString());
        Assert.Equal("high", fields.GetProperty("priority").GetString());
        Assert.Equal("2026-10-08", fields.GetProperty("startDate").GetString());
        var nextId = next.Id();
        Assert.False(Assert.Single((await GetAsync(_client, $"{Item(ws, list, nextId)}/checklist")).GetProperty("value").EnumerateArray()).GetProperty("done").GetBoolean());

        // The rule moves to the new task right after it is created.
        await EventuallyAsync(async () => (await _client.GetAsync($"{Item(ws, list, nextId)}/recurrence", Ct)).StatusCode == HttpStatusCode.OK ? true : (bool?)null);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Item(ws, list, task)}/recurrence", Ct)).StatusCode);
    }

    [Fact]
    public async Task Tasks_can_be_created_from_documents()
    {
        var ws = await Api.CreateWorkspaceAsync(_client, "Finance");
        var tasks = await ListAsync(_client, ws, "Tasks", "tasks");
        var library = await ListAsync(_client, ws, "Invoices", "documents");
        var document = await TaskAsync(_client, ws, library, new { title = "Invoice 17" });

        using var created = await _client.PostAsJsonAsync($"{Item(ws, library, document)}/tasks", new { workspaceId = ws, listId = tasks, dueDate = "2026-11-01" }, Ct);
        var task = await created.JsonAsync(HttpStatusCode.Created);
        Assert.Equal("Invoice 17", task.GetProperty("title").GetString());
        Assert.Equal("2026-11-01", task.GetProperty("dueDate").GetString());

        var ofDocument = (await GetAsync(_client, $"{Item(ws, library, document)}/tasks")).GetProperty("value");
        Assert.Equal(task.GetProperty("itemId").GetString(), Assert.Single(ofDocument.EnumerateArray()).GetProperty("itemId").GetString());
        var links = await GetAsync(_client, $"{Item(ws, tasks, task.GetProperty("itemId").GetString()!)}/links");
        Assert.Equal(document, Assert.Single(links.GetProperty("documents").EnumerateArray()).GetProperty("itemId").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"{Item(ws, library, document)}/tasks", new { workspaceId = ws, listId = library }, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_task_create_activity_creates_one_task_per_run()
    {
        var me = await MeAsync(_client);
        var ws = await Api.CreateWorkspaceAsync(_client, "Ops");
        var tasks = await ListAsync(_client, ws, "Follow-ups", "tasks");
        var tickets = (await Api.CreateListAsync(_client, ws, "Tickets")).Id();
        using var workflow = await _client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/workflows", new
        {
            name = "Follow up",
            definition = new
            {
                trigger = new { type = "itemAdded", list = "Tickets" },
                flow = new
                {
                    start = "task",
                    nodes = new { task = new { activity = "task.create", inputs = new { list = "Follow-ups", title = "Check {title}", assignedTo = new[] { "creator" }, dueInDays = 2, priority = "high" } } },
                },
            },
        }, Ct);
        await workflow.JsonAsync(HttpStatusCode.Created);
        await Api.CreateItemAsync(_client, ws, tickets, new { title = "Printer" });

        var task = await EventuallyAsync(async () =>
        {
            var items = (await GetAsync(_client, Api.Items(ws, tasks))).GetProperty("value");
            return items.GetArrayLength() == 0 ? (JsonElement?)null : items[0];
        });
        var fields = task.GetProperty("fields");
        Assert.Equal("Check Printer", fields.GetProperty("title").GetString());
        Assert.Equal("high", fields.GetProperty("priority").GetString());
        Assert.Equal([me], fields.GetProperty("assignedTo").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(Day(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2))), fields.GetProperty("dueDate").GetString());
    }

    [Fact]
    public async Task Task_features_are_isolated_by_tenant()
    {
        var ws = await Api.CreateWorkspaceAsync(_client, "Private");
        var list = await ListAsync(_client, ws, "Tasks", "tasks");
        var task = await TaskAsync(_client, ws, list, new { title = "Secret plan", assignedTo = new[] { await MeAsync(_client) } });
        var other = await _host.CreateTenantAsync("other");

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Item(ws, list, task)}/checklist", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Item(ws, list, task)}/links", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Item(ws, list, task)}/recurrence", new { rule = "FREQ=DAILY" }, Ct)).StatusCode);
        Assert.Equal(0, (await GetAsync(other, "/v1.0/me/tasks?view=all")).GetProperty("value").GetArrayLength());
    }
}
