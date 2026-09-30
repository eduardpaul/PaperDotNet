using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Tasks (phase 4a): checklists, links, cross-list views, recurrence, tasks from documents (TSK-01…06).</summary>
public sealed class TaskTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> TaskListAsync(HttpClient client, Guid ws, string name = "Tasks")
    {
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey = "tasks" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> TaskAsync(HttpClient client, Guid ws, Guid list, object fields) =>
        (await client.CreateItemAsync(ws, list, new { fields })).GetProperty("id").GetGuid();

    private static string Item(Guid ws, Guid list, Guid item) => $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}";

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<Guid> MeAsync(HttpClient client) => (await (await client.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();

    [Fact]
    public async Task Checklists_and_links_keep_tasks_consistent()
    {
        await factory.CreateTenantAsync("tasks-links");
        var client = await ApiClient.CreateAsync(factory, "tasks-links");
        var ws = await client.CreateWorkspaceAsync("Project");
        var list = await TaskListAsync(client, ws);
        var a = await TaskAsync(client, ws, list, new { title = "Release" });
        var b = await TaskAsync(client, ws, list, new { title = "Write notes" });
        var c = await TaskAsync(client, ws, list, new { title = "Test" });

        var checklist = await client.PutAsJsonAsync($"{Item(ws, list, a)}/checklist", new[] { new { text = "Tag", done = true }, new { text = "Publish", done = false } }, Ct);
        Assert.Equal(HttpStatusCode.OK, checklist.StatusCode);
        var entries = (await (await client.GetAsync($"{Item(ws, list, a)}/checklist", Ct)).ReadJsonAsync()).GetProperty("value");
        Assert.Equal(["Tag", "Publish"], entries.EnumerateArray().Select(e => e.GetProperty("text").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Item(ws, list, a)}/checklist", new[] { new { text = " ", done = false } }, Ct)).StatusCode);

        Task<HttpResponseMessage> LinkAsync(Guid from, string kind, Guid to) =>
            client.PostAsJsonAsync($"{Item(ws, list, from)}/links", new { kind, workspaceId = ws, listId = list, itemId = to }, Ct);

        Assert.Equal(HttpStatusCode.Created, (await LinkAsync(a, "subtask", b)).StatusCode);
        Assert.Equal("cycle", (await (await LinkAsync(b, "subtask", a)).ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal("hasParent", (await (await LinkAsync(c, "subtask", b)).ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, (await LinkAsync(a, "blockedBy", c)).StatusCode);
        Assert.Equal("cycle", (await (await LinkAsync(c, "blockedBy", a)).ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await LinkAsync(a, "document", c)).StatusCode);

        var ofB = await (await client.GetAsync($"{Item(ws, list, b)}/links", Ct)).ReadJsonAsync();
        Assert.Equal(a, ofB.GetProperty("parent").GetProperty("itemId").GetGuid());
        var ofC = await (await client.GetAsync($"{Item(ws, list, c)}/links", Ct)).ReadJsonAsync();
        var blocking = Assert.Single(ofC.GetProperty("blocking").EnumerateArray());
        Assert.Equal("Release", blocking.GetProperty("title").GetString());

        var linkId = blocking.GetProperty("linkId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Item(ws, list, c)}/links/{linkId}", Ct)).StatusCode);
        Assert.Equal(0, (await (await client.GetAsync($"{Item(ws, list, a)}/links", Ct)).ReadJsonAsync()).GetProperty("blockedBy").GetArrayLength());

        // Only task lists have task features.
        var notes = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Notes", templateKey = "notes" }, Ct);
        var notesList = (await notes.ReadJsonAsync()).GetProperty("id").GetGuid();
        var note = await TaskAsync(client, ws, notesList, new { title = "Idea" });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Item(ws, notesList, note)}/checklist", Ct)).StatusCode);
    }

    [Fact]
    public async Task My_tasks_span_every_task_list()
    {
        await factory.CreateTenantAsync("tasks-mine");
        var client = await ApiClient.CreateAsync(factory, "tasks-mine");
        var me = await MeAsync(client);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var ws1 = await client.CreateWorkspaceAsync("Home");
        var ws2 = await client.CreateWorkspaceAsync("Work");
        var home = await TaskListAsync(client, ws1);
        var work = await TaskListAsync(client, ws2, "Sprint");
        await TaskAsync(client, ws1, home, new { title = "Due today", dueDate = Day(today), assignedTo = new[] { me } });
        await TaskAsync(client, ws2, work, new { title = "Late", dueDate = Day(today.AddDays(-2)), assignedTo = new[] { me } });
        await TaskAsync(client, ws2, work, new { title = "Later", dueDate = Day(today.AddDays(40)), assignedTo = new[] { me } });
        await TaskAsync(client, ws2, work, new { title = "Done", dueDate = Day(today), status = "completed", assignedTo = new[] { me } });
        await TaskAsync(client, ws1, home, new { title = "Someone's", dueDate = Day(today) });

        async Task<List<string>> ViewAsync(string view)
        {
            var body = await (await client.GetAsync($"/v1.0/me/tasks?view={view}", Ct)).ReadJsonAsync();
            return body.GetProperty("value").EnumerateArray().Select(t => t.GetProperty("title").GetString()!).ToList();
        }

        Assert.Equal(["Late", "Due today", "Later"], await ViewAsync("mine"));
        Assert.Equal(["Late"], await ViewAsync("overdue"));
        Assert.Equal(["Due today"], await ViewAsync("dueThisWeek"));
        Assert.Equal(4, (await ViewAsync("all")).Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1.0/me/tasks?view=nope", Ct)).StatusCode);

        var first = (await (await client.GetAsync("/v1.0/me/tasks", Ct)).ReadJsonAsync()).GetProperty("value")[0];
        Assert.Equal("Sprint", first.GetProperty("listName").GetString());
    }

    [Fact]
    public async Task Completing_a_repeating_task_creates_the_next_one()
    {
        await factory.CreateTenantAsync("tasks-repeat");
        var client = await ApiClient.CreateAsync(factory, "tasks-repeat");
        var ws = await client.CreateWorkspaceAsync("Chores");
        var list = await TaskListAsync(client, ws);
        var noDue = await TaskAsync(client, ws, list, new { title = "Someday" });
        var task = await TaskAsync(client, ws, list, new { title = "Water plants", startDate = "2026-10-01", dueDate = "2026-10-05", priority = "high" });
        await client.PutAsJsonAsync($"{Item(ws, list, task)}/checklist", new[] { new { text = "Balcony", done = true } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Item(ws, list, task)}/recurrence", new { rule = "FREQ=SOMETIMES" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Item(ws, list, noDue)}/recurrence", new { rule = "FREQ=DAILY" }, Ct)).StatusCode);
        var set = await (await client.PutAsJsonAsync($"{Item(ws, list, task)}/recurrence", new { rule = "RRULE:FREQ=WEEKLY;BYDAY=MO" }, Ct)).ReadJsonAsync();
        Assert.Equal("2026-10-12", set.GetProperty("nextDueDate").GetString());

        var etag = (await client.GetAsync(Item(ws, list, task), Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await client.SendWithEtagAsync(HttpMethod.Patch, Item(ws, list, task), etag, new { fields = new { status = "completed" } })).StatusCode);

        JsonElement next = default;
        await Eventually.WaitForAsync<bool>(async () =>
        {
            var items = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items?$filter=fields/dueDate eq 2026-10-12", Ct)).ReadJsonAsync();
            if (items.GetProperty("value").GetArrayLength() == 0)
            {
                return null;
            }

            next = items.GetProperty("value")[0];
            return true;
        });

        var fields = next.GetProperty("fields");
        Assert.Equal("Water plants", fields.GetProperty("title").GetString());
        Assert.Equal("notStarted", fields.GetProperty("status").GetString());
        Assert.Equal("high", fields.GetProperty("priority").GetString());
        Assert.Equal("2026-10-08", fields.GetProperty("startDate").GetString());
        var nextId = next.GetProperty("id").GetGuid();
        var checklist = (await (await client.GetAsync($"{Item(ws, list, nextId)}/checklist", Ct)).ReadJsonAsync()).GetProperty("value");
        Assert.False(Assert.Single(checklist.EnumerateArray()).GetProperty("done").GetBoolean());
        // The rule moves to the new task right after it is created.
        await Eventually.WaitForAsync(async () => (await client.GetAsync($"{Item(ws, list, nextId)}/recurrence", Ct)).StatusCode == HttpStatusCode.OK ? true : (bool?)null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Item(ws, list, task)}/recurrence", Ct)).StatusCode);
    }

    [Fact]
    public async Task Tasks_can_be_created_from_documents()
    {
        await factory.CreateTenantAsync("tasks-docs");
        var client = await ApiClient.CreateAsync(factory, "tasks-docs");
        var ws = await client.CreateWorkspaceAsync("Finance");
        var tasks = await TaskListAsync(client, ws);
        var library = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Invoices", templateKey = "documents" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var upload = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{library}/documents",
            new MultipartFormDataContent { { new ByteArrayContent("%PDF-1.4\n% invoice\n%%EOF\n"u8.ToArray()), "file", "Invoice 17.pdf" } }, Ct);
        var document = (await upload.ReadJsonAsync()).GetProperty("itemId").GetGuid();

        var created = await client.PostAsJsonAsync($"{Item(ws, library, document)}/tasks", new { workspaceId = ws, listId = tasks, dueDate = "2026-11-01" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var task = await created.ReadJsonAsync();
        Assert.Equal("Invoice 17", task.GetProperty("title").GetString());
        Assert.Equal("2026-11-01", task.GetProperty("dueDate").GetString());

        var ofDocument = (await (await client.GetAsync($"{Item(ws, library, document)}/tasks", Ct)).ReadJsonAsync()).GetProperty("value");
        Assert.Equal(task.GetProperty("itemId").GetGuid(), Assert.Single(ofDocument.EnumerateArray()).GetProperty("itemId").GetGuid());
        var links = await (await client.GetAsync($"{Item(ws, tasks, task.GetProperty("itemId").GetGuid())}/links", Ct)).ReadJsonAsync();
        Assert.Equal(document, Assert.Single(links.GetProperty("documents").EnumerateArray()).GetProperty("itemId").GetGuid());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Item(ws, library, document)}/tasks", new { workspaceId = ws, listId = library }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Task_features_are_isolated_by_tenant()
    {
        await factory.CreateTenantAsync("tasks-iso-a");
        await factory.CreateTenantAsync("tasks-iso-b");
        var a = await ApiClient.CreateAsync(factory, "tasks-iso-a");
        var b = await ApiClient.CreateAsync(factory, "tasks-iso-b");
        var ws = await a.CreateWorkspaceAsync("Private");
        var list = await TaskListAsync(a, ws);
        var task = await TaskAsync(a, ws, list, new { title = "Secret plan", assignedTo = new[] { await MeAsync(a) } });

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"{Item(ws, list, task)}/checklist", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"{Item(ws, list, task)}/links", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"{Item(ws, list, task)}/recurrence", new { rule = "FREQ=DAILY" }, Ct)).StatusCode);
        var all = await (await b.GetAsync("/v1.0/me/tasks?view=all", Ct)).ReadJsonAsync();
        Assert.Equal(0, all.GetProperty("value").GetArrayLength());
    }
}
