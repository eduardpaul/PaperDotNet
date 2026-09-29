using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>Workflow triggers of slice 9b (ADR-0036): schedules, dates, module triggers, terms and manual inputs (EVT-10, EVT-11).</summary>
public sealed class WorkflowTriggerTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TenantSummary Tenant, HttpClient Admin, Guid Workspace, Guid Tasks)
    {
        public string Workflows => $"/v1.0/workspaces/{Workspace}/workflows";

        public string List(Guid list) => $"/v1.0/workspaces/{Workspace}/lists/{list}";
    }

    private async Task<Setup> SetupAsync(string tenantName)
    {
        var tenant = await factory.CreateTenantAsync(tenantName);
        var admin = await ApiClient.CreateAsync(factory, tenantName);
        var ws = await admin.CreateWorkspaceAsync("Office");
        var tasks = await PostIdAsync(admin, $"/v1.0/workspaces/{ws}/lists", new { name = "Tasks", templateKey = "tasks" });
        return new Setup(tenant, admin, ws, tasks);
    }

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.ReadJsonAsync();
    }

    private static List<JsonElement> Values(JsonElement page) => page.GetProperty("value").EnumerateArray().ToList();

    private static async Task<List<JsonElement>> RunsAsync(Setup s, Guid workflow, Func<List<JsonElement>, bool>? until = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var runs = Values(await GetAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}"));
            if (until is null || until(runs))
            {
                return runs;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(string.Join(", ", runs.Select(r => r.ToString())));
            }

            await Task.Delay(200, Ct);
        }
    }

    private static bool AllCompleted(List<JsonElement> runs) => runs.All(r => r.GetProperty("status").GetString() == "completed");

    private static object[] Note(string list, string title) =>
        [new { type = "action", action = "task.create", inputs = new { list, title } }];

    private async Task TickAsync(Setup s, Func<WorkflowsDbContext, Task>? before = null)
    {
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier);
        if (before is not null)
        {
            await before(scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>());
        }

        await scope.ServiceProvider.GetRequiredService<WorkflowScheduleJob>().RunAsync(Ct);
    }

    [Fact]
    public async Task Schedules_start_once_per_occurrence()
    {
        var s = await SetupAsync("wf-schedule");
        var workflow = await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Every morning",
            trigger = new { type = "schedule", cron = "0 8 * * *", timeZone = "Europe/Berlin" },
            steps = Note("Tasks", "Plan the day ({trigger:occurrence})"),
        });

        // The first tick only plans the next occurrence (8:00 in Berlin).
        await TickAsync(s);
        Assert.Empty(await RunsAsync(s, workflow));
        DateTimeOffset planned = default;
        await TickAsync(s, async db =>
        {
            var state = await db.Schedules.SingleAsync(x => x.Id == workflow, Ct);
            planned = state.NextAt!.Value;
            Assert.Equal(8, TimeZoneInfo.ConvertTime(planned, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).Hour);
            state.NextAt = DateTimeOffset.UtcNow.AddMinutes(-1); // as if 8:00 had come
            await db.SaveChangesAsync(Ct);
        });
        var runs = await RunsAsync(s, workflow, r => r.Count == 1 && AllCompleted(r));
        Assert.StartsWith("Plan the day (", (await s.Admin.QueryTitlesAsync(s.Workspace, s.Tasks, "")).Single(), StringComparison.Ordinal);

        // The same occurrence never starts twice; the next is planned after now.
        await TickAsync(s, async db =>
        {
            var state = await db.Schedules.SingleAsync(x => x.Id == workflow, Ct);
            Assert.True(state.NextAt > DateTimeOffset.UtcNow);
            state.NextAt = DateTimeOffset.Parse(runs[0].GetProperty("startedAt").GetString()!, CultureInfo.InvariantCulture).AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
        });

        // Schedules are checked when saved.
        var invalid = await s.Admin.PostAsJsonAsync(s.Workflows, new { name = "Bad", trigger = new { type = "schedule", cron = "every day" }, steps = Note("Tasks", "x") }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("cron", await invalid.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var withList = await s.Admin.PostAsJsonAsync(s.Workflows, new { name = "Bad", trigger = new { type = "schedule", cron = "0 8 * * *", list = "Tasks" }, steps = Note("Tasks", "x") }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, withList.StatusCode);
    }

    [Fact]
    public async Task Date_triggers_start_once_per_item_and_date()
    {
        var s = await SetupAsync("wf-dates");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        async Task<Guid> TaskAsync(string title, DateOnly due) =>
            (await s.Admin.CreateItemAsync(s.Workspace, s.Tasks, new { fields = new { title, dueDate = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } })).GetProperty("id").GetGuid();

        await TaskAsync("Due today", today);
        await TaskAsync("Due tomorrow", today.AddDays(1));
        await TaskAsync("Due next week", today.AddDays(7));
        await TaskAsync("Overdue", today.AddDays(-5));
        var followUps = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Follow-ups", templateKey = "tasks" });
        var workflow = await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Day before",
            trigger = new { type = "date", list = "Tasks", field = "dueDate", offsetHours = -24 },
            condition = "fields/status ne 'completed'",
            steps = Note("Follow-ups", "Remind: {title} ({trigger:date})"),
        });

        // Only dates reached after the workflow was saved count; move that back three days to reach today and tomorrow.
        await TickAsync(s);
        await TickAsync(s, async db =>
        {
            var state = await db.Schedules.SingleAsync(x => x.Id == workflow, Ct);
            state.CheckedUntil = DateTimeOffset.UtcNow.AddDays(-3);
            await db.SaveChangesAsync(Ct);
        });
        await RunsAsync(s, workflow, r => r.Count == 2 && AllCompleted(r));
        Assert.Equal(
            [$"Remind: Due today ({today:yyyy-MM-dd})", $"Remind: Due tomorrow ({today.AddDays(1):yyyy-MM-dd})"],
            (await s.Admin.QueryTitlesAsync(s.Workspace, followUps, "")).Order(StringComparer.Ordinal));

        // Checking the same window again starts nothing new (one run per item and date).
        await TickAsync(s, async db =>
        {
            var state = await db.Schedules.SingleAsync(x => x.Id == workflow, Ct);
            state.CheckedUntil = DateTimeOffset.UtcNow.AddDays(-3);
            await db.SaveChangesAsync(Ct);
        });
        Assert.Equal(2, (await RunsAsync(s, workflow)).Count);

        // Date and time fields: an event starting within the next hour, one hour before it.
        var calendar = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Calendar", templateKey = "calendar" });
        var soon = DateTimeOffset.UtcNow.AddMinutes(30);
        await s.Admin.CreateItemAsync(s.Workspace, calendar, new { fields = new { title = "Standup", start = soon.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) } });
        await s.Admin.CreateItemAsync(s.Workspace, calendar, new { fields = new { title = "Later", start = soon.AddHours(5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) } });
        var reminder = await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Hour before",
            trigger = new { type = "date", list = "Calendar", field = "start", offsetHours = -1 },
            steps = Note("Follow-ups", "Soon: {title}"),
        });
        await TickAsync(s);
        await TickAsync(s, async db =>
        {
            var state = await db.Schedules.SingleAsync(x => x.Id == reminder, Ct);
            state.CheckedUntil = DateTimeOffset.UtcNow.AddHours(-1);
            await db.SaveChangesAsync(Ct);
        });
        await RunsAsync(s, reminder, r => r.Count == 1 && AllCompleted(r));
        Assert.Contains("Soon: Standup", await s.Admin.QueryTitlesAsync(s.Workspace, followUps, ""));

        // Date triggers need a date field of the list.
        var invalid = await s.Admin.PostAsJsonAsync(s.Workflows, new { name = "Bad", trigger = new { type = "date", list = "Tasks", field = "title" }, steps = Note("Tasks", "x") }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("no date field 'title'", await invalid.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_triggers_start_workflows_on_tasks_comments_approvals_and_documents()
    {
        var s = await SetupAsync("wf-modules");
        var log = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Log", templateKey = "tasks" });
        var catalog = (await GetAsync(s.Admin, "/v1.0/workflows/triggers")).EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.Superset(new HashSet<string?>(["schedule", "date", "document.processed", "approval.decided", "task.completed", "comment.added"]), catalog.ToHashSet());

        await PostIdAsync(s.Admin, s.Workflows, new { name = "Done", trigger = new { type = "task.completed", list = "Tasks" }, steps = Note("Log", "Done: {title}") });
        await PostIdAsync(s.Admin, s.Workflows, new { name = "Commented", trigger = new { type = "comment.added", list = "Tasks" }, steps = Note("Log", "Comment on {title}: {trigger:text}") });
        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Ask",
            trigger = new { type = "manual", list = "Tasks" },
            steps = new object[] { new { type = "approval", name = "Boss", assignees = new[] { "creator" } } },
        });
        await PostIdAsync(s.Admin, s.Workflows, new { name = "Decided", trigger = new { type = "approval.decided" }, steps = Note("Log", "{trigger:step} {trigger:outcome} for {title} ({trigger:workflow})") });

        var task = (await s.Admin.CreateItemAsync(s.Workspace, s.Tasks, new { fields = new { title = "Write report" } })).GetProperty("id").GetGuid();
        var item = $"{s.List(s.Tasks)}/items/{task}";
        Assert.True((await s.Admin.PostAsJsonAsync($"{item}/comments", new { text = "Looks good" }, Ct)).IsSuccessStatusCode);
        Assert.True((await s.Admin.PostAsJsonAsync($"{item}/workflows", new { workflow = "Ask" }, Ct)).IsSuccessStatusCode);
        var approval = await Eventually.WaitForAsync(async () =>
            Values(await GetAsync(s.Admin, "/v1.0/me/approvals")).Select(a => (Guid?)a.GetProperty("id").GetGuid()).FirstOrDefault(), TimeSpan.FromSeconds(30));
        Assert.True((await s.Admin.PostAsJsonAsync($"/v1.0/me/approvals/{approval}/decision", new { outcome = "approved" }, Ct)).IsSuccessStatusCode);
        var complete = await s.Admin.SendWithEtagAsync(HttpMethod.Patch, item, (await s.Admin.GetAsync(item, Ct)).Headers.ETag!.Tag, new { fields = new { status = "completed" } });
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync(Ct));

        var expected = new[] { "Boss approved for Write report (Ask)", "Comment on Write report: Looks good", "Done: Write report" };
        await Eventually.WaitForAsync(async () =>
            expected.All((await s.Admin.QueryTitlesAsync(s.Workspace, log, "")).Contains) ? true : (bool?)null, TimeSpan.FromSeconds(30));

        // Documents: the trigger fires once the text is there, with what processing found.
        var library = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Archive", templateKey = "documents" });
        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Processed",
            trigger = new { type = "document.processed", list = "Archive" },
            steps = Note("Log", "Processed {title}: {trigger:pageCount} page(s), OCR {trigger:ocr}"),
        });
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddText("Invoice for the walrus", 20, new PdfPoint(40, 760), builder.AddStandard14Font(Standard14Font.Helvetica));
        var upload = await s.Admin.PostAsync($"{s.List(library)}/documents",
            new MultipartFormDataContent { { new ByteArrayContent(builder.Build()), "file", "walrus.pdf" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var document = (await upload.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        var title = (await GetAsync(s.Admin, $"{s.List(library)}/items/{document}")).GetProperty("fields").GetProperty("title").GetString();
        await Eventually.WaitForAsync(async () =>
            (await s.Admin.QueryTitlesAsync(s.Workspace, log, "")).Contains($"Processed {title}: 1 page(s), OCR false") ? true : (bool?)null, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Terms_narrow_triggers_and_manual_starts_take_inputs_and_selections()
    {
        var s = await SetupAsync("wf-terms");
        var group = await PostIdAsync(s.Admin, "/v1.0/termStore/groups", new { name = "Documents" });
        var tags = await PostIdAsync(s.Admin, "/v1.0/termStore/sets", new { groupId = group, name = "Tags" });
        var receipt = await PostIdAsync(s.Admin, $"/v1.0/termStore/sets/{tags}/terms", new { name = "Receipt" });
        var grocery = await PostIdAsync(s.Admin, $"/v1.0/termStore/sets/{tags}/terms", new { name = "Grocery", parentId = receipt });
        var other = await PostIdAsync(s.Admin, $"/v1.0/termStore/sets/{tags}/terms", new { name = "Contract" });
        var type = await s.Admin.CreateContentTypeAsync("Paper", [new { name = "tag", type = "managedMetadata", termSetId = tags }, new { name = "note", type = "text" }]);
        var papers = await s.Admin.CreateListAsync(s.Workspace, "Papers", type);

        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Receipts",
            trigger = new { type = "itemAdded", list = "Papers", terms = new[] { "Documents/Tags/Receipt" } },
            steps = new[] { new { type = "action", action = "item.update", inputs = new { fields = new { note = "receipt" } } } },
        });
        async Task<string?> NoteAsync(Guid id) =>
            (await GetAsync(s.Admin, $"{s.List(papers)}/items/{id}")).GetProperty("fields").TryGetProperty("note", out var note) ? note.GetString() : null;

        var shop = (await s.Admin.CreateItemAsync(s.Workspace, papers, new { fields = new { title = "Shop", tag = grocery } })).GetProperty("id").GetGuid();
        var lease = (await s.Admin.CreateItemAsync(s.Workspace, papers, new { fields = new { title = "Lease", tag = other } })).GetProperty("id").GetGuid();
        await Eventually.WaitForAsync(async () => await NoteAsync(shop) == "receipt" ? true : (bool?)null, TimeSpan.FromSeconds(30));
        Assert.Null(await NoteAsync(lease));
        var unknownTerm = await s.Admin.PostAsJsonAsync(s.Workflows, new
        {
            name = "Bad",
            trigger = new { type = "itemAdded", list = "Papers", terms = new[] { "Documents/Tags/Nope" } },
            steps = Note("Tasks", "x"),
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknownTerm.StatusCode);

        // Manual starts: inputs (checked against the trigger's inputs) become variables; a selection starts one run per item.
        var stamp = await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Stamp",
            trigger = new
            {
                type = "manual",
                list = "Papers",
                inputs = new { properties = new { label = new { type = "string" }, copies = new { type = "integer" } }, required = new[] { "label" } },
            },
            steps = new[] { new { type = "action", action = "item.update", inputs = new { fields = new { note = "{var:label} x{var:copies}" } } } },
        });
        async Task<HttpResponseMessage> StartAsync(object body) => await s.Admin.PostAsJsonAsync($"{s.Workflows}/{stamp}/runs", body, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(new { listId = papers, itemIds = new[] { shop, lease } })).StatusCode); // label is required
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(new { listId = papers, itemIds = new[] { shop }, inputs = new { label = "Paid", copies = "two" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(new { inputs = new { label = "Paid" } })).StatusCode); // the trigger needs items
        var started = await StartAsync(new { listId = papers, itemIds = new[] { shop, lease }, inputs = new { label = "Paid", copies = 2 } });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(2, (await started.ReadJsonAsync()).GetArrayLength());
        await Eventually.WaitForAsync(async () => await NoteAsync(lease) == "Paid x2" && await NoteAsync(shop) == "Paid x2" ? true : (bool?)null, TimeSpan.FromSeconds(30));

        // Without a list, a manual workflow runs without an item.
        var summary = await PostIdAsync(s.Admin, s.Workflows, new { name = "Summary", trigger = new { type = "manual" }, steps = Note("Tasks", "Summary") });
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsJsonAsync($"{s.Workflows}/{summary}/runs", new { }, Ct)).StatusCode);
        await Eventually.WaitForAsync(async () => (await s.Admin.QueryTitlesAsync(s.Workspace, s.Tasks, "")).Contains("Summary") ? true : (bool?)null, TimeSpan.FromSeconds(30));

        // Other tenants cannot start it.
        await factory.CreateTenantAsync("wf-terms-b");
        var foreign = await ApiClient.CreateAsync(factory, "wf-terms-b");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"{s.Workflows}/{summary}/runs", new { }, Ct)).StatusCode);
    }
}
