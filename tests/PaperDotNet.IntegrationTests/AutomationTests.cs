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

/// <summary>Automations (phase 5b, ADR-0024): triggers, path templates, approvals, delays, extension triggers and actions (EVT-07…09, DOC-14).</summary>
public sealed class AutomationTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TenantSummary Tenant, HttpClient Admin, Guid Workspace, Guid Invoices, Guid Tasks, Dictionary<string, Guid> Users)
    {
        public string Automations => $"/v1.0/workspaces/{Workspace}/automations";

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

    private static object Action(string action, object inputs) => new { type = "action", action, inputs };

    [Fact]
    public async Task Item_triggers_file_items_create_tasks_and_notify()
    {
        var s = await SetupAsync("auto-rules");
        var automation = await PostIdAsync(s.Admin, s.Automations, new
        {
            name = "File bills",
            trigger = new { type = "itemAdded", list = "Bills", contentType = "Bill" },
            condition = "fields/amount gt 100",
            steps = new[]
            {
                Action("item.file", new { folder = "{created:yyyy}/{counterparty}", title = "{counterparty} ({amount})" }),
                Action("task.create", new { list = "Tasks", title = "Check {title}", assignedTo = new[] { "alice" }, dueInDays = 3 }),
                Action("notify", new { to = new[] { "alice", "creator" }, title = "Filed: {title}" }),
            },
        });

        var big = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "scan-1", counterparty = "ACME/Corp", amount = 500 } })).GetProperty("id").GetGuid();
        var small = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "scan-2", counterparty = "Tiny", amount = 5 } })).GetProperty("id").GetGuid();

        // Only the item that matches the condition gets a run.
        var runs = await WaitAsync(s.Admin, $"{s.Automations}/runs?automationId={automation}", r => Values(r).Count == 1 && Status(Values(r)[0]) == "completed");
        var run = Values(runs).Single();
        Assert.Equal(big, run.GetProperty("itemId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, run.GetProperty("eventId").ValueKind);
        await Task.Delay(500, Ct);
        Assert.Empty(Values(await GetAsync(s.Admin, $"{s.Automations}/runs?itemId={small}")));

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

        // A condition that can no longer be checked gives a visible failed run.
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            var version = await db.Versions.SingleAsync(v => v.AutomationId == automation, Ct);
            version.Definition = version.Definition.Replace("fields/amount gt 100", "fields/gone gt 1", StringComparison.Ordinal);
            await db.SaveChangesAsync(Ct);
        }

        var broken = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "scan-3", amount = 1 } })).GetProperty("id").GetGuid();
        var failed = Values(await WaitAsync(s.Admin, $"{s.Automations}/runs?itemId={broken}", r => Values(r).Count == 1)).Single();
        Assert.Equal("failed", Status(failed));
        Assert.StartsWith("condition:", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restored_items_trigger_automations_and_old_runs_are_cleaned_up()
    {
        var s = await SetupAsync("auto-restore");
        var automation = await PostIdAsync(s.Admin, s.Automations, new
        {
            name = "Restored",
            trigger = new { type = "itemRestored", list = "Bills" },
            steps = new[] { Action("item.update", new { fields = new { note = "restored" } }) },
        });
        var bill = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "Bill" } })).GetProperty("id").GetGuid();
        var etag = (await s.Admin.GetAsync(s.Item(bill), Ct)).Headers.ETag!.Tag;
        Assert.True((await s.Admin.SendWithEtagAsync(HttpMethod.Delete, s.Item(bill), etag)).IsSuccessStatusCode);
        var restore = await s.Admin.PostAsync($"/v1.0/workspaces/{s.Workspace}/lists/{s.Invoices}/recycleBin/{bill}/restore", null, Ct);
        Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync(Ct));
        await WaitAsync(s.Admin, s.Item(bill), i => i.GetProperty("fields").TryGetProperty("note", out var note) && note.GetString() == "restored");

        var run = Values(await WaitAsync(s.Admin, $"{s.Automations}/runs?automationId={automation}", r => Values(r).Count == 1 && Status(Values(r)[0]) == "completed")).Single();
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<AutomationRunCleanupJob>();
        await job.RunAsync(Ct);
        Assert.True(await db.Runs.AnyAsync(r => r.Id == run.GetProperty("id").GetGuid(), Ct));
        var stored = await db.Runs.SingleAsync(r => r.Id == run.GetProperty("id").GetGuid(), Ct);
        stored.CompletedAt = DateTimeOffset.UtcNow.AddDays(-31);
        await db.SaveChangesAsync(Ct);
        await job.RunAsync(Ct);
        Assert.False(await db.Runs.AnyAsync(r => r.AutomationId == automation, Ct));
    }

    [Fact]
    public async Task Automations_that_change_their_own_items_stop()
    {
        var s = await SetupAsync("auto-loop");
        var automation = await PostIdAsync(s.Admin, s.Automations, new
        {
            name = "Touch",
            trigger = new { type = "itemUpdated", list = "Bills" },
            steps = new[] { Action("item.update", new { fields = new { note = "touched {modified:HH:mm:ss.fffffff}" } }) },
        });
        var bill = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "loop", amount = 1 } })).GetProperty("id").GetGuid();
        var etag = (await s.Admin.GetAsync(s.Item(bill), Ct)).Headers.ETag!.Tag;
        Assert.True((await s.Admin.SendWithEtagAsync(HttpMethod.Patch, s.Item(bill), etag, new { fields = new { amount = 2 } })).IsSuccessStatusCode);

        // The user's change (depth 0) and two automatic ones start runs; the third automatic change is not reacted to.
        await WaitAsync(s.Admin, $"{s.Automations}/runs?automationId={automation}", r => Values(r).Count >= 3);
        await Task.Delay(3000, Ct);
        Assert.Equal(3, Values(await GetAsync(s.Admin, $"{s.Automations}/runs?automationId={automation}")).Count);
    }

    [Fact]
    public async Task Manual_automations_wait_for_approvals_escalate_and_branch()
    {
        var s = await SetupAsync("auto-flow");
        var workflow = await PostIdAsync(s.Admin, s.Automations, new
        {
            name = "Bill approval",
            trigger = new { type = "manual", list = "Bills" },
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
            var run = await PostIdAsync(s.Admin, $"{s.Item(item)}/automations", new { automation = "Bill approval" });
            await WaitAsync(s.Admin, $"{s.Automations}/runs/{run}", r => Status(r) == "waiting");
            return (item, run);
        }

        // Approved: alice decides, the run continues and notifies the creator.
        var (first, firstRun) = await StartAsync("Bill 1");
        var approval = Values(await GetAsync(alice, "/v1.0/me/approvals")).Single();
        Assert.Equal("Approve Bill 1", approval.GetProperty("title").GetString());
        await WaitAsync(alice, "/v1.0/me/notifications", n => Values(n).Any(x => x.GetProperty("title").GetString() == "Approve Bill 1"));
        Assert.Empty(Values(await GetAsync(bob, "/v1.0/me/approvals")));
        var approvalId = approval.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/v1.0/me/approvals/{approvalId}/decision", new { outcome = "approved" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/v1.0/me/approvals/{approvalId}/decision", new { outcome = "approved", comment = "fine" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync($"/v1.0/me/approvals/{approvalId}/decision", new { outcome = "rejected" }, Ct)).StatusCode);
        var completed = await WaitAsync(s.Admin, $"{s.Automations}/runs/{firstRun}", r => Status(r) is "completed" or "failed");
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
        await WaitAsync(s.Admin, $"{s.Automations}/runs/{secondRun}", r => Status(r) == "completed");
        Assert.Equal("Rejected", (await GetAsync(s.Admin, s.Item(second))).GetProperty("fields").GetProperty("state").GetString());

        // Cancelled runs cancel their approvals; a new version does not change started runs.
        var (_, thirdRun) = await StartAsync("Bill 3");
        var put = new HttpRequestMessage(HttpMethod.Put, $"{s.Automations}/{workflow}")
        {
            Content = JsonContent.Create(new { name = "Bill approval", trigger = new { type = "manual" }, steps = new object[] { new { type = "delay", hours = 1 } } }),
        };
        put.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue((await s.Admin.GetAsync($"{s.Automations}/{workflow}", Ct)).Headers.ETag!.Tag));
        Assert.Equal(2, (await (await s.Admin.SendAsync(put, Ct)).ReadJsonAsync()).GetProperty("version").GetInt32());
        var cancelled = await (await s.Admin.PostAsync($"{s.Automations}/runs/{thirdRun}/cancel", null, Ct)).ReadJsonAsync();
        Assert.Equal("cancelled", Status(cancelled));
        Assert.Equal(1, cancelled.GetProperty("automationVersion").GetInt32());
        Assert.Empty(Values(await GetAsync(alice, "/v1.0/me/approvals")));
        Assert.Equal(3, Values(await GetAsync(s.Admin, $"{s.Automations}/runs?automationId={workflow}")).Count);

        // Delays: the minute job resumes runs whose time has come.
        await PostIdAsync(s.Admin, s.Automations, new
        {
            name = "Later",
            trigger = new { type = "manual" },
            steps = new object[]
            {
                new { type = "delay", hours = 24 },
                new { type = "action", action = "item.update", inputs = new { fields = new { note = "a day later" } } },
            },
        });
        var later = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "Bill 4", amount = 1 } })).GetProperty("id").GetGuid();
        var laterRun = await PostIdAsync(s.Admin, $"{s.Item(later)}/automations", new { automation = "Later" });
        await WaitAsync(s.Admin, $"{s.Automations}/runs/{laterRun}", r => Status(r) == "waiting");
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

        await WaitAsync(s.Admin, $"{s.Automations}/runs/{laterRun}", r => Status(r) == "completed");
        Assert.Equal("a day later", (await GetAsync(s.Admin, s.Item(later))).GetProperty("fields").GetProperty("note").GetString());
    }

    [Fact]
    public async Task Runs_are_claimed_recovered_and_repeat_safe()
    {
        var s = await SetupAsync("auto-reliable");
        var automation = await PostIdAsync(s.Admin, s.Automations, new
        {
            name = "Make task",
            trigger = new { type = "manual" },
            steps = new[] { Action("task.create", new { list = "Tasks", title = "Follow up {title}" }) },
        });
        var bill = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "Bill" } })).GetProperty("id").GetGuid();
        var factoryScopes = factory.Services.GetRequiredService<ITenantScopeFactory>();

        // A repeated action execution (same execution id) creates the task once.
        await using (var scope = factoryScopes.CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var executor = scope.ServiceProvider.GetRequiredService<ActionExecutor>();
            var action = new ActionDefinition("task.create", new System.Text.Json.Nodes.JsonObject { ["list"] = "Tasks", ["title"] = "Once" });
            var executionId = Guid.CreateVersion7();
            for (var i = 0; i < 2; i++)
            {
                var result = await executor.ExecuteAsync(action, s.Workspace, null, null, null, new Dictionary<string, string>(), "test", "test:1", executionId, Ct);
                Assert.True(result.Succeeded, result.Error);
                Assert.Equal(executionId.ToString(), result.Output!["taskId"]!.GetValue<string>());
            }
        }

        Assert.Equal(["Once"], await s.Admin.QueryTitlesAsync(s.Workspace, s.Tasks, ""));

        // A run whose start message was lost (no lease, no progress) is recovered by the timer job.
        Guid lost;
        await using (var scope = factoryScopes.CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            var old = DateTimeOffset.UtcNow.AddMinutes(-10);
            lost = Guid.CreateVersion7();
            db.Runs.Add(new AutomationRun
            {
                Id = lost,
                AutomationId = automation,
                AutomationVersion = 1,
                WorkspaceId = s.Workspace,
                ListId = s.Invoices,
                ItemId = bill,
                Status = RunStatus.Running,
                StartedAt = old,
                LastActivityAt = old,
            });
            await db.SaveChangesAsync(Ct);

            // A run another handler holds is not executed twice: the message is retried later.
            var interpreter = scope.ServiceProvider.GetRequiredService<AutomationInterpreter>();
            await db.Runs.Where(r => r.Id == lost).ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(1)), Ct);
            await Assert.ThrowsAsync<RunLeasedException>(() => interpreter.RunAsync(lost, null, Ct));
            await scope.ServiceProvider.GetRequiredService<AutomationTimerJob>().RunAsync(Ct);
            Assert.Equal(RunStatus.Running, (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == lost, Ct)).Status);

            await db.Runs.Where(r => r.Id == lost).ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null), Ct);
            await scope.ServiceProvider.GetRequiredService<AutomationTimerJob>().RunAsync(Ct);
        }

        var recovered = await WaitAsync(s.Admin, $"{s.Automations}/runs/{lost}", r => Status(r) == "completed");
        Assert.Contains("Follow up Bill", await s.Admin.QueryTitlesAsync(s.Workspace, s.Tasks, ""));
        Assert.Equal(automation, recovered.GetProperty("automationId").GetGuid());

        // A run that keeps failing without progress is given up.
        await using (var scope = factoryScopes.CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            var stuck = Guid.CreateVersion7();
            db.Runs.Add(new AutomationRun
            {
                Id = stuck,
                AutomationId = automation,
                AutomationVersion = 1,
                WorkspaceId = s.Workspace,
                ListId = s.Invoices,
                ItemId = bill,
                Status = RunStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow,
                Attempts = AutomationInterpreter.MaxAttempts,
            });
            await db.SaveChangesAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<AutomationInterpreter>().RunAsync(stuck, null, Ct);
            var failed = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == stuck, Ct);
            Assert.Equal(RunStatus.Failed, failed.Status);
            Assert.Contains("attempts", failed.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Extension_triggers_start_automations_that_use_extension_actions()
    {
        var s = await SetupAsync("auto-ext");
        Assert.True((await s.Admin.PostAsync("/v1.0/extensions/samples.invoices/enable", null, Ct)).IsSuccessStatusCode);
        var invoices = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Invoices", templateKey = "samples.invoices.invoices" });
        var automation = await PostIdAsync(s.Admin, s.Automations, new
        {
            name = "Large invoices",
            trigger = new { type = "samples.invoices.approvalNeeded" },
            steps = new object[]
            {
                new { type = "action", action = "samples.invoices.approve" },
                Action("item.update", new { fields = new { title = "Approved ({data:amount})" } }),
            },
        });

        var actions = await GetAsync(s.Admin, "/v1.0/automation/actions");
        Assert.Contains(actions.EnumerateArray(), a => a.GetProperty("key").GetString() == "samples.invoices.approve");
        Assert.Contains((await GetAsync(s.Admin, "/v1.0/automation/triggers")).EnumerateArray(), t => t.GetProperty("key").GetString() == "samples.invoices.approvalNeeded");

        var invoice = (await s.Admin.CreateItemAsync(s.Workspace, invoices, new { fields = new { title = "Big", amount = 5000 } })).GetProperty("id").GetGuid();
        var approved = await WaitAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists/{invoices}/items/{invoice}", i => i.GetProperty("fields").GetProperty("status").GetString() == "approved"
            && i.GetProperty("fields").GetProperty("title").GetString()!.StartsWith("Approved", StringComparison.Ordinal));
        Assert.Equal("Approved (5000)", approved.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("completed", Status(Values(await GetAsync(s.Admin, $"{s.Automations}/runs?automationId={automation}")).Single()));
    }

    [Fact]
    public async Task Automation_is_validated_needs_access_and_stays_in_the_tenant()
    {
        var s = await SetupAsync("auto-acl");
        await factory.CreateTenantAsync("auto-acl-b");
        var foreign = await ApiClient.CreateAsync(factory, "auto-acl-b");
        var carol = await ApiClient.CreateAsync(factory, "auto-acl", "carol", "carol-password-1");
        var notify = new[] { Action("notify", new { to = new[] { "alice" }, title = "t" }) };

        async Task<string> InvalidAsync(object automation)
        {
            var response = await s.Admin.PostAsJsonAsync(s.Automations, automation, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        Assert.Contains("Unknown action", await InvalidAsync(new { name = "x", trigger = new { type = "itemAdded" }, steps = new[] { new { type = "action", action = "nope" } } }), StringComparison.Ordinal);
        Assert.Contains("does not exist", await InvalidAsync(new { name = "x", trigger = new { type = "itemAdded", list = "Missing" }, steps = notify }), StringComparison.Ordinal);
        Assert.Contains("condition", await InvalidAsync(new { name = "x", trigger = new { type = "itemAdded", list = "Bills" }, condition = "fields/unknown eq 1", steps = notify }), StringComparison.Ordinal);
        Assert.Contains("Unknown trigger", await InvalidAsync(new { name = "x", trigger = new { type = "samples.invoices.nothing" }, steps = notify }), StringComparison.Ordinal);
        Assert.Contains("trigger is required", await InvalidAsync(new { name = "x", steps = notify }), StringComparison.Ordinal);
        Assert.Contains("earlier approval", await InvalidAsync(new { name = "w", trigger = new { type = "manual" }, steps = new object[] { new { type = "approval" }, new { type = "condition", step = "Later", @is = "approved" } } }), StringComparison.Ordinal);

        var valid = new { name = "Notify", trigger = new { type = "itemAdded", list = "Bills" }, steps = new[] { Action("notify", new { to = new[] { "alice" }, title = "New: {title}" }) } };
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.PostAsJsonAsync(s.Automations, valid, Ct)).StatusCode);
        var automation = await PostIdAsync(s.Admin, s.Automations, valid);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync(s.Automations, valid, Ct)).StatusCode);
        Assert.Single((await GetAsync(carol, s.Automations)).EnumerateArray());

        // Only manual automations are started by people, and only where their trigger allows.
        var bill = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "Bill", amount = 1 } })).GetProperty("id").GetGuid();
        async Task<string> StartFailsAsync(HttpClient client, string name)
        {
            var response = await client.PostAsJsonAsync($"{s.Item(bill)}/automations", new { automation = name }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        Assert.Contains("not started manually", await StartFailsAsync(s.Admin, "Notify"), StringComparison.Ordinal);
        await PostIdAsync(s.Admin, s.Automations, new { name = "Tasks only", trigger = new { type = "manual", list = "Tasks" }, steps = notify });
        Assert.Contains("only runs on items of the list", await StartFailsAsync(s.Admin, "Tasks only"), StringComparison.Ordinal);
        await PostIdAsync(s.Admin, s.Automations, new { name = "Big only", trigger = new { type = "manual", list = "Bills" }, condition = "fields/amount gt 100", steps = notify });
        Assert.Contains("does not match", await StartFailsAsync(s.Admin, "Big only"), StringComparison.Ordinal);
        Assert.Contains("does not match", await StartFailsAsync(carol, "Big only"), StringComparison.Ordinal); // members may start them

        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(s.Automations, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{s.Automations}/{automation}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync(s.Automations, new { name = "w", trigger = new { type = "manual" }, steps = new object[] { new { type = "delay", hours = 1 } } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{s.Automations}/runs", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"{s.Item(bill)}/automations", new { automation = "Big only" }, Ct)).StatusCode);
        Assert.Empty(Values(await GetAsync(foreign, "/v1.0/me/approvals")));

        // Automations travel with workspace templates (by name) and apply idempotently.
        var xml = await (await s.Admin.GetAsync($"/v1.0/provisioning/export?workspaceId={s.Workspace}", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("urn:paperdotnet:automation:2", xml, StringComparison.Ordinal);
        var apply = await foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.True(apply.IsSuccessStatusCode, await apply.Content.ReadAsStringAsync(Ct));
        var foreignWs = Values(await GetAsync(foreign, "/v1.0/workspaces")).Single(w => w.GetProperty("name").GetString() == "Finance").GetProperty("id").GetGuid();
        Assert.Equal(["Big only", "Notify", "Tasks only"], (await GetAsync(foreign, $"/v1.0/workspaces/{foreignWs}/automations")).EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        var again = await foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.Empty((await again.ReadJsonAsync()).GetProperty("changes").EnumerateArray());
    }
}
