using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Workflow triggers (EVT-07…11): schedules, dates, module triggers, terms, several triggers and manual inputs.</summary>
public sealed class WorkflowTriggerTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private Guid _tenant;
    private string _workspace = "";
    private string _notes = "";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _tenant = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Office");
        _notes = (await Api.CreateListAsync(_admin, _workspace, "Log", new object[] { new { name = "source", type = "text" } })).Id();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private string Workflows => $"/v1.0/workspaces/{_workspace}/workflows";

    private async Task<string> CreateWorkflowAsync(string name, string definition)
    {
        using var response = await _admin.PostAsJsonAsync(Workflows, new { name, definition = JsonNode.Parse(definition) }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static string Log(string title) => $$"""
        "flow": { "start": "log", "nodes": { "log": { "activity": "item.create", "inputs": { "list": "Log", "fields": { "title": "{{title}}" } } } } }
        """;

    private async Task<List<string?>> LogAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var titles = (await (await _admin.GetAsync(Api.Items(_workspace, _notes), Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
                .Select(i => i.GetProperty("fields").GetProperty("title").GetString()).Order(StringComparer.Ordinal).ToList();
            if (titles.Count >= count)
            {
                return titles;
            }

            Assert.True(DateTime.UtcNow < deadline, $"{titles.Count} of {count}: {string.Join(", ", titles)}");
            await Task.Delay(100, Ct);
        }
    }

    private async Task JobAsync<TJob>(Func<WorkflowsDbContext, Task>? change = null)
        where TJob : PaperDotNet.Jobs.Contracts.ITenantRecurringJob
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        if (change is not null)
        {
            await change(db);
            await db.SaveChangesAsync(Ct);
        }

        await ActivatorUtilities.CreateInstance<TJob>(scope.ServiceProvider).RunAsync(_tenant, Ct);
    }

    [Fact]
    public async Task Schedules_start_once_per_occurrence_and_dates_once_per_item_and_date()
    {
        var schedule = await CreateWorkflowAsync("Morning", $$"""{ "trigger": { "type": "schedule", "cron": "0 8 * * *", "timeZone": "Europe/Berlin" }, {{Log("morning {data:occurrence}")}} }""");
        var events = (await Api.CreateListAsync(_admin, _workspace, "Events", new object[] { new { name = "due", type = "date" } })).Id();
        await CreateWorkflowAsync("Due", $$"""{ "trigger": { "type": "date", "list": "Events", "field": "due", "offsetHours": 0 }, {{Log("due {title}")}} }""");
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await Api.CreateItemAsync(_admin, _workspace, events, new { title = "Past", due = yesterday });
        await Api.CreateItemAsync(_admin, _workspace, events, new { title = "Later", due = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });

        // The first pass only records where each trigger is (nothing is due yet: counting starts when the workflow was saved).
        await JobAsync<WorkflowScheduleJob>();
        var scheduleId = WorkflowScheduleJob.StateId(Guid.Parse(schedule), 0);
        await JobAsync<WorkflowScheduleJob>(async db =>
        {
            foreach (var state in await db.WorkflowSchedules.Where(s => s.TenantId == _tenant).ToListAsync(Ct))
            {
                if (state.Id == scheduleId)
                {
                    Assert.True(state.NextAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    state.NextAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
                }
                else
                {
                    state.CheckedUntilUnixMs = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
                }
            }
        });

        var log = await LogAsync(2);
        Assert.Equal("due Past", log[0]);
        Assert.StartsWith("morning ", log[1], StringComparison.Ordinal);

        // Passing again starts nothing new.
        await JobAsync<WorkflowScheduleJob>(async db =>
        {
            var state = await db.WorkflowSchedules.SingleAsync(s => s.TenantId == _tenant && s.Id != scheduleId, Ct);
            state.CheckedUntilUnixMs = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeMilliseconds();
        });
        await Task.Delay(500, Ct);
        Assert.Equal(2, (await LogAsync(2)).Count);

        // Invalid schedules are refused.
        using var invalid = await _admin.PostAsJsonAsync(Workflows, new { name = "Bad", definition = JsonNode.Parse($$"""{ "trigger": { "type": "schedule", "cron": "every day" }, {{Log("x")}} }""") }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Module_triggers_start_workflows_on_tasks_comments_and_approvals()
    {
        var catalog = (await (await _admin.GetAsync("/v1.0/workflows/triggers", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.Contains("task.completed", catalog);
        Assert.Contains("comment.added", catalog);
        Assert.Contains("approval.decided", catalog);
        Assert.Contains("schedule", catalog);

        using (var created = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/lists", new { name = "Todo", templateKey = "tasks" }, Ct))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var todo = (await (await _admin.GetAsync($"/v1.0/workspaces/{_workspace}/lists", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Todo").Id();
        await CreateWorkflowAsync("Done", $$"""{ "trigger": { "type": "task.completed", "list": "Todo" }, {{Log("completed {title}")}} }""");
        await CreateWorkflowAsync("Commented", $$"""{ "trigger": { "type": "comment.added", "data": { "reply": false } }, {{Log("comment on {title}: {data:text}")}} }""");
        await CreateWorkflowAsync("Decided", $$"""{ "trigger": { "type": "approval.decided" }, {{Log("{data:workflow} {data:outcome}")}} }""");
        await CreateWorkflowAsync("Ask", """
            { "trigger": { "type": "manual", "list": "Todo" }, "flow": { "start": "ask", "nodes": {
              "ask": { "activity": "approval", "inputs": { "assignees": ["admin"] } } } } }
            """);

        var task = (await Api.CreateItemAsync(_admin, _workspace, todo, new { title = "Write docs" })).Id();
        var etag = (await _admin.GetAsync($"{Api.Items(_workspace, todo)}/{task}", Ct)).Headers.ETag!.Tag;
        using (var completed = await _admin.SendAsync(Api.Patch($"{Api.Items(_workspace, todo)}/{task}", new { fields = new { status = "completed" } }, etag), Ct))
        {
            Assert.True(completed.IsSuccessStatusCode, await completed.Content.ReadAsStringAsync(Ct));
        }

        var comment = await _admin.PostAsJsonAsync($"{Api.Items(_workspace, todo)}/{task}/comments", new { text = "Looks good" }, Ct);
        var commentId = (await comment.JsonAsync(HttpStatusCode.Created)).Id();
        using (var reply = await _admin.PostAsJsonAsync($"{Api.Items(_workspace, todo)}/{task}/comments", new { text = "Thanks", parentId = commentId }, Ct))
        {
            await reply.JsonAsync(HttpStatusCode.Created);
        }

        var ask = (await (await _admin.GetAsync(Workflows, Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("name").GetString() == "Ask").Id();
        using (var started = await _admin.PostAsJsonAsync($"{Workflows}/{ask}/runs", new { listId = todo, itemId = task }, Ct))
        {
            await started.JsonAsync(HttpStatusCode.Accepted);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement pending;
        while ((pending = (await (await _admin.GetAsync("/v1.0/me/approvals?status=pending", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value")).GetArrayLength() == 0)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }

        using (var decided = await _admin.PostAsJsonAsync($"/v1.0/me/approvals/{pending[0].Id()}/decision", new { outcome = "approved" }, Ct))
        {
            await decided.JsonAsync(HttpStatusCode.OK);
        }

        Assert.Equal(["Ask approved", "comment on Write docs: Looks good", "completed Write docs"], await LogAsync(3));
        await Task.Delay(300, Ct);
        Assert.Equal(3, (await LogAsync(3)).Count);
    }

    [Fact]
    public async Task Terms_narrow_triggers_several_triggers_start_once_and_manual_starts_take_inputs_and_selections()
    {
        var group = await PostIdAsync("/v1.0/termStore/groups", new { name = "Documents" });
        var tags = await PostIdAsync("/v1.0/termStore/sets", new { groupId = group, name = "Tags" });
        var receipt = await PostIdAsync($"/v1.0/termStore/sets/{tags}/terms", new { name = "Receipt" });
        var grocery = await PostIdAsync($"/v1.0/termStore/sets/{tags}/terms", new { name = "Grocery", parentId = receipt });
        var other = await PostIdAsync($"/v1.0/termStore/sets/{tags}/terms", new { name = "Contract" });
        var paperType = await Api.CreateContentTypeAsync(_admin, "Paper", new object[]
        {
            new { name = "tag", displayName = "Tag", type = "managedMetadata", termSetId = tags },
            new { name = "note", displayName = "Note", type = "text" },
        });
        using (var list = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/lists", new { name = "Papers", contentTypeIds = new[] { paperType } }, Ct))
        {
            await list.JsonAsync(HttpStatusCode.Created);
        }

        var papers = (await (await _admin.GetAsync($"/v1.0/workspaces/{_workspace}/lists", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Papers").Id();
        await CreateWorkflowAsync("Receipts", """
            { "triggers": [ { "type": "itemAdded", "list": "Papers", "terms": ["Documents/Tags/Receipt"] }, { "type": "itemAdded", "list": "Papers", "contentType": "Paper" } ],
              "flow": { "start": "mark", "nodes": { "mark": { "activity": "item.update", "inputs": { "fields": { "note": "{note}+" } } } } } }
            """);
        async Task<string?> NoteAsync(string id) =>
            (await (await _admin.GetAsync($"{Api.Items(_workspace, papers)}/{id}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("fields").TryGetProperty("note", out var note) ? note.GetString() : null;

        // Both triggers match the shop receipt: the workflow starts once.
        var shop = (await Api.CreateItemAsync(_admin, _workspace, papers, new { title = "Shop", tag = grocery })).Id();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await NoteAsync(shop) != "+")
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }

        await Task.Delay(300, Ct);
        Assert.Equal("+", await NoteAsync(shop));
        using (var unknownTerm = await _admin.PostAsJsonAsync(Workflows, new { name = "Bad", definition = JsonNode.Parse("""
            { "trigger": { "type": "itemAdded", "terms": ["Documents/Tags/Nope"] }, "flow": { "start": "e", "nodes": { "e": { "activity": "end" } } } }
            """) }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownTerm.StatusCode);
        }

        // Only terms: a contract does not match.
        await CreateWorkflowAsync("Only receipts", """
            { "trigger": { "type": "itemUpdated", "list": "Papers", "terms": ["Documents/Tags/Receipt"], "changedFields": ["title"] },
              "flow": { "start": "mark", "nodes": { "mark": { "activity": "item.create", "inputs": { "list": "Log", "fields": { "title": "receipt {title}" } } } } } }
            """);
        var lease = (await Api.CreateItemAsync(_admin, _workspace, papers, new { title = "Lease", tag = other })).Id();

        // The lease matches the content type trigger of "Receipts": wait for its update before changing it.
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (await NoteAsync(lease) != "+")
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }

        foreach (var id in new[] { shop, lease })
        {
            var etag = (await _admin.GetAsync($"{Api.Items(_workspace, papers)}/{id}", Ct)).Headers.ETag!.Tag;
            using var renamed = await _admin.SendAsync(Api.Patch($"{Api.Items(_workspace, papers)}/{id}", new { fields = new { title = $"{id[..4]} renamed" } }, etag), Ct);
            Assert.True(renamed.IsSuccessStatusCode);
        }

        Assert.Equal([$"receipt {shop[..4]} renamed"], await LogAsync(1));

        // Manual starts: inputs (checked against the trigger's inputs) become variables; a selection starts one run per item.
        var stamp = await CreateWorkflowAsync("Stamp", """
            { "trigger": { "type": "manual", "list": "Papers",
                           "inputs": { "properties": { "label": { "type": "string" }, "copies": { "type": "integer" } }, "required": ["label"] } },
              "flow": { "start": "mark", "nodes": { "mark": { "activity": "item.update", "inputs": { "fields": { "note": "{var:label} x{var:copies}" } } } } } }
            """);
        async Task<HttpResponseMessage> StartAsync(object body) => await _admin.PostAsJsonAsync($"{Workflows}/{stamp}/runs", body, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(new { listId = papers, itemIds = new[] { shop, lease } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(new { listId = papers, itemIds = new[] { shop }, inputs = new { label = "Paid", copies = "two" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(new { inputs = new { label = "Paid" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(new { listId = _notes, itemIds = new[] { shop }, inputs = new { label = "Paid" } })).StatusCode);
        using (var started = await StartAsync(new { listId = papers, itemIds = new[] { shop, lease }, inputs = new { label = "Paid", copies = 2 } }))
        {
            Assert.Equal(2, (await started.JsonAsync(HttpStatusCode.OK)).GetArrayLength());
        }

        deadline = DateTime.UtcNow.AddSeconds(30);
        while (await NoteAsync(shop) != "Paid x2" || await NoteAsync(lease) != "Paid x2")
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(100, Ct);
        }

        // Other tenants cannot start it.
        var foreign = await _host.CreateTenantAsync("triggers-other");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"{Workflows}/{stamp}/runs", new { }, Ct)).StatusCode);
    }

    private async Task<string> PostIdAsync(string url, object body)
    {
        using var response = await _admin.PostAsJsonAsync(url, body, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }
}
