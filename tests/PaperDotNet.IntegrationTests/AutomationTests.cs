using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Data;
using PaperDotNet.Automation.Features;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Automation (phase 5b): rules, path templates, workflows with approvals, extension triggers and actions (EVT-07…09, DOC-14).</summary>
public sealed class AutomationTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TenantSummary Tenant, HttpClient Admin, Guid Workspace, Guid Invoices, Guid Tasks, Dictionary<string, Guid> Users)
    {
        public string Automation => $"/v1.0/workspaces/{Workspace}/automation";

        public string Item(Guid id) => $"/v1.0/workspaces/{Workspace}/lists/{Invoices}/items/{id}";
    }

    private async Task<Setup> SetupAsync(string tenantName)
    {
        var tenant = await factory.CreateTenantAsync(tenantName);
        var admin = await ApiClient.CreateAsync(factory, tenantName);
        var users = new Dictionary<string, Guid>();
        foreach (var name in new[] { "alice", "bob", "carol" })
        {
            users[name] = await PostIdAsync(admin, "/v1.0/users", new { userName = name, password = $"{name}-password-1" });
        }

        var ws = await admin.CreateWorkspaceAsync("Finance");
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = users["carol"], role = "member" }, Ct);
        var invoice = await admin.CreateContentTypeAsync("Bill", [
            new { name = "counterparty", type = "text" },
            new { name = "amount", type = "number" },
            new { name = "state", type = "choice", choices = new[] { "new", "Approved", "Rejected" } },
            new { name = "note", type = "text" },
        ]);
        var invoices = await admin.CreateListAsync(ws, "Bills", invoice);
        var tasks = await PostIdAsync(admin, $"/v1.0/workspaces/{ws}/lists", new { name = "Tasks", templateKey = "tasks" });
        return new Setup(tenant, admin, ws, invoices, tasks, users);
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

    private static async Task<JsonElement> WaitAsync(HttpClient client, string url, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var body = await GetAsync(client, url);
            if (condition(body))
            {
                return body;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{url}: {body}");
            }

            await Task.Delay(200, Ct);
        }
    }

    private static string Status(JsonElement run) => run.GetProperty("status").GetString()!;

    private static List<JsonElement> Values(JsonElement page) => page.GetProperty("value").EnumerateArray().ToList();

    [Fact]
    public async Task Rules_file_items_create_tasks_and_notify()
    {
        var s = await SetupAsync("auto-rules");
        var rule = await PostIdAsync(s.Admin, $"{s.Automation}/rules", new
        {
            name = "File bills",
            trigger = new { type = "itemAdded", list = "Bills", contentType = "Bill" },
            condition = "fields/amount gt 100",
            actions = new object[]
            {
                new { type = "item.file", inputs = new { folder = "{created:yyyy}/{counterparty}", title = "{counterparty} ({amount})" } },
                new { type = "task.create", inputs = new { list = "Tasks", title = "Check {title}", assignedTo = new[] { "alice" }, dueInDays = 3 } },
                new { type = "notify", inputs = new { to = new[] { "alice", "creator" }, title = "Filed: {title}" } },
            },
        });

        var big = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "scan-1", counterparty = "ACME/Corp", amount = 500 } })).GetProperty("id").GetGuid();
        var small = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "scan-2", counterparty = "Tiny", amount = 5 } })).GetProperty("id").GetGuid();

        var runs = await WaitAsync(s.Admin, $"{s.Automation}/rules/{rule}/runs", r => Values(r).Count == 2 && Values(r).All(x => Status(x) != "running"));
        Assert.Equal("completed", Status(Values(runs).Single(r => r.GetProperty("itemId").GetGuid() == big)));
        Assert.Equal("skipped", Status(Values(runs).Single(r => r.GetProperty("itemId").GetGuid() == small)));

        // Path template: folders from the created year and the counterparty ('/' is not allowed in names), then renamed.
        var filed = await GetAsync(s.Admin, s.Item(big));
        Assert.Equal("ACME/Corp (500)", filed.GetProperty("fields").GetProperty("title").GetString());
        var folder = await GetAsync(s.Admin, s.Item(filed.GetProperty("parentId").GetGuid()));
        Assert.Equal("ACME-Corp", folder.GetProperty("fields").GetProperty("title").GetString());
        var year = await GetAsync(s.Admin, s.Item(folder.GetProperty("parentId").GetGuid()));
        Assert.Equal(DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), year.GetProperty("fields").GetProperty("title").GetString());
        Assert.False((await GetAsync(s.Admin, s.Item(small))).TryGetProperty("parentId", out _));

        var tasks = await s.Admin.QueryTitlesAsync(s.Workspace, s.Tasks, "");
        Assert.Equal(["Check ACME/Corp (500)"], tasks);
        var alice = await ApiClient.CreateAsync(factory, "auto-rules", "alice", "alice-password-1");
        var inbox = Values(await GetAsync(alice, "/v1.0/me/notifications"));
        Assert.Contains(inbox, n => n.GetProperty("title").GetString() == "Filed: ACME/Corp (500)");
        Assert.Contains(Values(await GetAsync(s.Admin, "/v1.0/me/notifications")), n => n.GetProperty("title").GetString() == "Filed: ACME/Corp (500)");
    }

    [Fact]
    public async Task Rules_that_change_their_own_items_stop()
    {
        var s = await SetupAsync("auto-loop");
        var rule = await PostIdAsync(s.Admin, $"{s.Automation}/rules", new
        {
            name = "Touch",
            trigger = new { type = "itemUpdated", list = "Bills" },
            actions = new object[] { new { type = "item.update", inputs = new { fields = new { note = "touched {modified:HH:mm:ss.fffffff}" } } } },
        });
        var bill = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "loop", amount = 1 } })).GetProperty("id").GetGuid();
        var etag = (await s.Admin.GetAsync(s.Item(bill), Ct)).Headers.ETag!.Tag;
        Assert.True((await s.Admin.SendWithEtagAsync(HttpMethod.Patch, s.Item(bill), etag, new { fields = new { amount = 2 } })).IsSuccessStatusCode);

        // The user's change (depth 0) and two automatic ones run the rule; the third automatic change is not reacted to.
        await WaitAsync(s.Admin, $"{s.Automation}/rules/{rule}/runs", r => Values(r).Count >= 3);
        await Task.Delay(3000, Ct);
        Assert.Equal(3, Values(await GetAsync(s.Admin, $"{s.Automation}/rules/{rule}/runs")).Count);
    }

    [Fact]
    public async Task Workflows_wait_for_approvals_escalate_and_branch()
    {
        var s = await SetupAsync("auto-flow");
        var workflow = await PostIdAsync(s.Admin, $"{s.Automation}/workflows", new
        {
            name = "Bill approval",
            steps = new object[]
            {
                new { type = "approval", name = "Manager", assignees = new[] { "alice" }, title = "Approve {title}", dueInHours = 48, escalateTo = new[] { "bob" } },
                new
                {
                    type = "condition", step = "Manager", @is = "approved",
                    then = new object[] { new { type = "action", action = "item.update", inputs = new { fields = new { state = "Approved" } } } },
                    @else = new object[] { new { type = "action", action = "item.update", inputs = new { fields = new { state = "Rejected" } } } },
                },
                new { type = "action", action = "notify", inputs = new { to = new[] { "creator" }, title = "{title}: {outcome:Manager}" } },
            },
        });
        var alice = await ApiClient.CreateAsync(factory, "auto-flow", "alice", "alice-password-1");
        var bob = await ApiClient.CreateAsync(factory, "auto-flow", "bob", "bob-password-1");

        async Task<(Guid Item, Guid Run)> StartAsync(string title)
        {
            var item = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title, amount = 10 } })).GetProperty("id").GetGuid();
            var run = await PostIdAsync(s.Admin, $"{s.Item(item)}/workflows", new { workflow = "Bill approval" });
            await WaitAsync(s.Admin, $"{s.Automation}/runs/{run}", r => Status(r) == "waiting");
            return (item, run);
        }

        // Approved: alice decides, the run continues and notifies the creator.
        var (first, firstRun) = await StartAsync("Bill 1");
        var approval = Values(await GetAsync(alice, "/v1.0/me/approvals")).Single();
        Assert.Equal("Approve Bill 1", approval.GetProperty("title").GetString());
        Assert.Empty(Values(await GetAsync(bob, "/v1.0/me/approvals")));
        var approvalId = approval.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/v1.0/me/approvals/{approvalId}/decision", new { outcome = "approved" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/v1.0/me/approvals/{approvalId}/decision", new { outcome = "approved", comment = "fine" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync($"/v1.0/me/approvals/{approvalId}/decision", new { outcome = "rejected" }, Ct)).StatusCode);
        var completed = await WaitAsync(s.Admin, $"{s.Automation}/runs/{firstRun}", r => Status(r) is "completed" or "failed");
        Assert.Equal("completed", Status(completed));
        Assert.Equal("approved", completed.GetProperty("outcomes").GetProperty("Manager").GetString());
        Assert.Equal("Approved", (await GetAsync(s.Admin, s.Item(first))).GetProperty("fields").GetProperty("state").GetString());
        Assert.Contains(Values(await GetAsync(s.Admin, "/v1.0/me/notifications")), n => n.GetProperty("title").GetString() == "Bill 1: approved");

        // Overdue: the escalation job adds bob, who rejects.
        var (second, secondRun) = await StartAsync("Bill 2");
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            var pending = await db.Approvals.SingleAsync(a => a.RunId == secondRun, Ct);
            pending.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<AutomationTimerJob>().RunAsync(Ct);
        }

        var escalated = Values(await GetAsync(bob, "/v1.0/me/approvals")).Single();
        Assert.True(escalated.GetProperty("escalated").GetBoolean());
        await bob.PostAsJsonAsync($"/v1.0/me/approvals/{escalated.GetProperty("id").GetGuid()}/decision", new { outcome = "rejected" }, Ct);
        await WaitAsync(s.Admin, $"{s.Automation}/runs/{secondRun}", r => Status(r) == "completed");
        Assert.Equal("Rejected", (await GetAsync(s.Admin, s.Item(second))).GetProperty("fields").GetProperty("state").GetString());

        // Cancelled runs cancel their approvals; a new version does not change started runs.
        var (_, thirdRun) = await StartAsync("Bill 3");
        var put = new HttpRequestMessage(HttpMethod.Put, $"{s.Automation}/workflows/{workflow}")
        {
            Content = JsonContent.Create(new { name = "Bill approval", steps = new object[] { new { type = "delay", hours = 1 } } }),
        };
        put.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue((await s.Admin.GetAsync($"{s.Automation}/workflows/{workflow}", Ct)).Headers.ETag!.Tag));
        Assert.Equal(2, (await (await s.Admin.SendAsync(put, Ct)).ReadJsonAsync()).GetProperty("version").GetInt32());
        var cancelled = await (await s.Admin.PostAsync($"{s.Automation}/runs/{thirdRun}/cancel", null, Ct)).ReadJsonAsync();
        Assert.Equal("cancelled", Status(cancelled));
        Assert.Equal(1, cancelled.GetProperty("workflowVersion").GetInt32());
        Assert.Empty(Values(await GetAsync(alice, "/v1.0/me/approvals")));
        Assert.Equal(3, Values(await GetAsync(s.Admin, $"{s.Automation}/runs?workflowId={workflow}")).Count);

        // Delays: the minute job resumes runs whose time has come.
        await PostIdAsync(s.Admin, $"{s.Automation}/workflows", new
        {
            name = "Later",
            steps = new object[]
            {
                new { type = "delay", hours = 24 },
                new { type = "action", action = "item.update", inputs = new { fields = new { note = "a day later" } } },
            },
        });
        var later = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "Bill 4", amount = 1 } })).GetProperty("id").GetGuid();
        var laterRun = await PostIdAsync(s.Admin, $"{s.Item(later)}/workflows", new { workflow = "Later" });
        await WaitAsync(s.Admin, $"{s.Automation}/runs/{laterRun}", r => Status(r) == "waiting");
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var job = scope.ServiceProvider.GetRequiredService<AutomationTimerJob>();
            await job.RunAsync(Ct);
            var db = scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            var run = await db.Runs.SingleAsync(r => r.Id == laterRun, Ct);
            Assert.Equal(RunStatus.Waiting, run.Status);
            run.ResumeAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
            await job.RunAsync(Ct);
        }

        await WaitAsync(s.Admin, $"{s.Automation}/runs/{laterRun}", r => Status(r) == "completed");
        Assert.Equal("a day later", (await GetAsync(s.Admin, s.Item(later))).GetProperty("fields").GetProperty("note").GetString());
    }

    [Fact]
    public async Task Extension_triggers_start_workflows_that_use_extension_actions()
    {
        var s = await SetupAsync("auto-ext");
        Assert.True((await s.Admin.PostAsync("/v1.0/extensions/samples.invoices/enable", null, Ct)).IsSuccessStatusCode);
        var invoices = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Invoices", templateKey = "samples.invoices.invoices" });
        await PostIdAsync(s.Admin, $"{s.Automation}/workflows", new
        {
            name = "Auto approve",
            steps = new object[] { new { type = "action", action = "samples.invoices.approve" } },
        });
        var rule = await PostIdAsync(s.Admin, $"{s.Automation}/rules", new
        {
            name = "Large invoices",
            trigger = new { type = "samples.invoices.approvalNeeded" },
            actions = new object[] { new { type = "workflow.start", inputs = new { workflow = "Auto approve" } } },
        });

        var actions = await GetAsync(s.Admin, "/v1.0/automation/actions");
        Assert.Contains(actions.EnumerateArray(), a => a.GetProperty("key").GetString() == "samples.invoices.approve");
        Assert.Contains((await GetAsync(s.Admin, "/v1.0/automation/triggers")).EnumerateArray(), t => t.GetProperty("key").GetString() == "samples.invoices.approvalNeeded");

        var invoice = (await s.Admin.CreateItemAsync(s.Workspace, invoices, new { fields = new { title = "Big", amount = 5000 } })).GetProperty("id").GetGuid();
        await WaitAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists/{invoices}/items/{invoice}", i => i.GetProperty("fields").GetProperty("status").GetString() == "approved");
        Assert.Equal("completed", Status(Values(await GetAsync(s.Admin, $"{s.Automation}/rules/{rule}/runs")).Single()));
    }

    [Fact]
    public async Task Automation_is_validated_needs_access_and_stays_in_the_tenant()
    {
        var s = await SetupAsync("auto-acl");
        await factory.CreateTenantAsync("auto-acl-b");
        var foreign = await ApiClient.CreateAsync(factory, "auto-acl-b");
        var carol = await ApiClient.CreateAsync(factory, "auto-acl", "carol", "carol-password-1");

        async Task<string> InvalidAsync(object rule)
        {
            var response = await s.Admin.PostAsJsonAsync($"{s.Automation}/rules", rule, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        Assert.Contains("Unknown action", await InvalidAsync(new { name = "x", trigger = new { type = "itemAdded" }, actions = new object[] { new { type = "nope" } } }), StringComparison.Ordinal);
        Assert.Contains("does not exist", await InvalidAsync(new { name = "x", trigger = new { type = "itemAdded", list = "Missing" }, actions = new object[] { new { type = "notify", inputs = new { to = new[] { "alice" }, title = "t" } } } }), StringComparison.Ordinal);
        Assert.Contains("condition", await InvalidAsync(new { name = "x", trigger = new { type = "itemAdded", list = "Bills" }, condition = "fields/unknown eq 1", actions = new object[] { new { type = "notify", inputs = new { to = new[] { "alice" }, title = "t" } } } }), StringComparison.Ordinal);
        Assert.Contains("Unknown trigger", await InvalidAsync(new { name = "x", trigger = new { type = "samples.invoices.nothing" }, actions = new object[] { new { type = "notify", inputs = new { to = new[] { "a" }, title = "t" } } } }), StringComparison.Ordinal);
        var badWorkflow = await s.Admin.PostAsJsonAsync($"{s.Automation}/workflows", new { name = "w", steps = new object[] { new { type = "approval" }, new { type = "condition", step = "Later", @is = "approved" } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badWorkflow.StatusCode);

        var valid = new { name = "Notify", trigger = new { type = "itemAdded", list = "Bills" }, actions = new object[] { new { type = "notify", inputs = new { to = new[] { "alice" }, title = "New: {title}" } } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.PostAsJsonAsync($"{s.Automation}/rules", valid, Ct)).StatusCode);
        var rule = await PostIdAsync(s.Admin, $"{s.Automation}/rules", valid);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync($"{s.Automation}/rules", valid, Ct)).StatusCode);
        Assert.Single((await GetAsync(carol, $"{s.Automation}/rules")).EnumerateArray());

        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{s.Automation}/rules", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{s.Automation}/rules/{rule}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"{s.Automation}/workflows", new { name = "w", steps = new object[] { new { type = "delay", hours = 1 } } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{s.Automation}/runs", Ct)).StatusCode);
        Assert.Empty(Values(await GetAsync(foreign, "/v1.0/me/approvals")));

        // Rules and workflows travel with workspace templates (by name) and apply idempotently.
        await PostIdAsync(s.Admin, $"{s.Automation}/workflows", new { name = "Wait", steps = new object[] { new { type = "delay", hours = 2 } } });
        var xml = await (await s.Admin.GetAsync($"/v1.0/provisioning/export?workspaceId={s.Workspace}", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("urn:paperdotnet:automation:1", xml, StringComparison.Ordinal);
        var apply = await foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.True(apply.IsSuccessStatusCode, await apply.Content.ReadAsStringAsync(Ct));
        var foreignWs = Values(await GetAsync(foreign, "/v1.0/workspaces")).Single(w => w.GetProperty("name").GetString() == "Finance").GetProperty("id").GetGuid();
        Assert.Equal(["Notify"], (await GetAsync(foreign, $"/v1.0/workspaces/{foreignWs}/automation/rules")).EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.Equal(["Wait"], (await GetAsync(foreign, $"/v1.0/workspaces/{foreignWs}/automation/workflows")).EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        var again = await foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.Empty((await again.ReadJsonAsync()).GetProperty("changes").EnumerateArray());
    }
}
