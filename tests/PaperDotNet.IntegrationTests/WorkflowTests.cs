using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Workflows (phase 5b, ADR-0024): triggers, path templates, approvals, delays, extension triggers and actions (EVT-07…09, DOC-14).</summary>
public sealed class WorkflowTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TenantSummary Tenant, HttpClient Admin, Guid Workspace, Guid Invoices, Guid Tasks, Dictionary<string, Guid> Users)
    {
        public string Workflows => $"/v1.0/workspaces/{Workspace}/workflows";

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
        var workflow = await PostIdAsync(s.Admin, s.Workflows, new
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
        var runs = await WaitAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}", r => Values(r).Count == 1 && Status(Values(r)[0]) == "completed");
        var run = Values(runs).Single();
        Assert.Equal(big, run.GetProperty("itemId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, run.GetProperty("eventId").ValueKind);
        await Task.Delay(500, Ct);
        Assert.Empty(Values(await GetAsync(s.Admin, $"{s.Workflows}/runs?itemId={small}")));
        Assert.Single(Values(await GetAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}&status=completed")));
        Assert.Empty(Values(await GetAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}&status=failed")));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync($"{s.Workflows}/runs?status=done", Ct)).StatusCode);

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
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var version = await db.Versions.SingleAsync(v => v.WorkflowId == workflow, Ct);
            version.Definition = version.Definition.Replace("fields/amount gt 100", "fields/gone gt 1", StringComparison.Ordinal);
            await db.SaveChangesAsync(Ct);
        }

        var broken = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "scan-3", amount = 1 } })).GetProperty("id").GetGuid();
        var failed = Values(await WaitAsync(s.Admin, $"{s.Workflows}/runs?itemId={broken}", r => Values(r).Count == 1)).Single();
        Assert.Equal("failed", Status(failed));
        Assert.StartsWith("condition:", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restored_items_trigger_workflows_and_old_runs_are_cleaned_up()
    {
        var s = await SetupAsync("auto-restore");
        var workflow = await PostIdAsync(s.Admin, s.Workflows, new
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

        var run = Values(await WaitAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}", r => Values(r).Count == 1 && Status(Values(r)[0]) == "completed")).Single();
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<WorkflowRunCleanupJob>();
        await job.RunAsync(Ct);
        Assert.True(await db.Runs.AnyAsync(r => r.Id == run.GetProperty("id").GetGuid(), Ct));
        var stored = await db.Runs.SingleAsync(r => r.Id == run.GetProperty("id").GetGuid(), Ct);
        stored.CompletedAt = DateTimeOffset.UtcNow.AddDays(-31);
        await db.SaveChangesAsync(Ct);
        await job.RunAsync(Ct);
        Assert.False(await db.Runs.AnyAsync(r => r.WorkflowId == workflow, Ct));
    }

    [Fact]
    public async Task Workflows_that_change_their_own_items_stop()
    {
        var s = await SetupAsync("auto-loop");
        var workflow = await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Touch",
            trigger = new { type = "itemUpdated", list = "Bills" },
            steps = new[] { Action("item.update", new { fields = new { note = "touched {modified:HH:mm:ss.fffffff}" } }) },
        });
        var bill = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "loop", amount = 1 } })).GetProperty("id").GetGuid();
        var etag = (await s.Admin.GetAsync(s.Item(bill), Ct)).Headers.ETag!.Tag;
        Assert.True((await s.Admin.SendWithEtagAsync(HttpMethod.Patch, s.Item(bill), etag, new { fields = new { amount = 2 } })).IsSuccessStatusCode);

        // The user's change (depth 0) and two automatic ones start runs; the third automatic change is not reacted to.
        await WaitAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}", r => Values(r).Count >= 3);
        await Task.Delay(3000, Ct);
        Assert.Equal(3, Values(await GetAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}")).Count);
    }

    [Fact]
    public async Task Manual_workflows_wait_for_approvals_escalate_and_branch()
    {
        var s = await SetupAsync("auto-flow");
        var workflow = await PostIdAsync(s.Admin, s.Workflows, new
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
            var run = await PostIdAsync(s.Admin, $"{s.Item(item)}/workflows", new { workflow = "Bill approval" });
            await WaitAsync(s.Admin, $"{s.Workflows}/runs/{run}", r => Status(r) == "waiting");
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
        // Enum filters take the documented camelCase names (as SDKs send them) in any case, and reject anything else.
        Assert.Equal(approvalId, Values(await GetAsync(alice, "/v1.0/me/approvals?status=approved")).Single().GetProperty("id").GetGuid());
        Assert.Single(Values(await GetAsync(alice, "/v1.0/me/approvals?status=Approved")));
        Assert.Empty(Values(await GetAsync(alice, "/v1.0/me/approvals?status=pending")));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/v1.0/me/approvals?status=1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/v1.0/me/approvals?status=maybe", Ct)).StatusCode);
        var completed = await WaitAsync(s.Admin, $"{s.Workflows}/runs/{firstRun}", r => Status(r) is "completed" or "failed");
        Assert.Equal("completed", Status(completed));
        Assert.Equal("approved", completed.GetProperty("outcomes").GetProperty("Manager").GetString());
        Assert.Equal("Approved", (await GetAsync(s.Admin, s.Item(first))).GetProperty("fields").GetProperty("state").GetString());
        Assert.Contains(Values(await GetAsync(s.Admin, "/v1.0/me/notifications")), n => n.GetProperty("title").GetString() == "Bill 1: approved");

        // Overdue: the escalation job adds bob, who rejects.
        var (second, secondRun) = await StartAsync("Bill 2");
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var pending = await db.Approvals.SingleAsync(a => a.RunId == secondRun, Ct);
            pending.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<WorkflowTimerJob>().RunAsync(Ct);
        }

        var escalated = Values(await GetAsync(bob, "/v1.0/me/approvals")).Single();
        Assert.True(escalated.GetProperty("escalated").GetBoolean());
        await bob.PostAsJsonAsync($"/v1.0/me/approvals/{escalated.GetProperty("id").GetGuid()}/decision", new { outcome = "rejected" }, Ct);
        await WaitAsync(s.Admin, $"{s.Workflows}/runs/{secondRun}", r => Status(r) == "completed");
        Assert.Equal("Rejected", (await GetAsync(s.Admin, s.Item(second))).GetProperty("fields").GetProperty("state").GetString());

        // Cancelled runs cancel their approvals; a new version does not change started runs.
        var (_, thirdRun) = await StartAsync("Bill 3");
        var put = new HttpRequestMessage(HttpMethod.Put, $"{s.Workflows}/{workflow}")
        {
            Content = JsonContent.Create(new { name = "Bill approval", trigger = new { type = "manual" }, steps = new object[] { new { type = "delay", hours = 1 } } }),
        };
        put.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue((await s.Admin.GetAsync($"{s.Workflows}/{workflow}", Ct)).Headers.ETag!.Tag));
        Assert.Equal(2, (await (await s.Admin.SendAsync(put, Ct)).ReadJsonAsync()).GetProperty("version").GetInt32());
        var cancelled = await (await s.Admin.PostAsync($"{s.Workflows}/runs/{thirdRun}/cancel", null, Ct)).ReadJsonAsync();
        Assert.Equal("cancelled", Status(cancelled));
        Assert.Equal(1, cancelled.GetProperty("workflowVersion").GetInt32());
        Assert.Empty(Values(await GetAsync(alice, "/v1.0/me/approvals")));
        Assert.Equal(3, Values(await GetAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}")).Count);

        // Delays: the minute job resumes runs whose time has come.
        await PostIdAsync(s.Admin, s.Workflows, new
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
        var laterRun = await PostIdAsync(s.Admin, $"{s.Item(later)}/workflows", new { workflow = "Later" });
        await WaitAsync(s.Admin, $"{s.Workflows}/runs/{laterRun}", r => Status(r) == "waiting");
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var job = scope.ServiceProvider.GetRequiredService<WorkflowTimerJob>();
            await job.RunAsync(Ct);
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var run = await db.Runs.SingleAsync(r => r.Id == laterRun, Ct);
            Assert.Equal(RunStatus.Waiting, run.Status);
            var delay = await db.Bookmarks.SingleAsync(b => b.Id == run.WaitingOn, Ct);
            Assert.Equal(BookmarkKinds.Delay, delay.Kind);
            delay.ResumeAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
            await job.RunAsync(Ct);
        }

        await WaitAsync(s.Admin, $"{s.Workflows}/runs/{laterRun}", r => Status(r) == "completed");
        Assert.Equal("a day later", (await GetAsync(s.Admin, s.Item(later))).GetProperty("fields").GetProperty("note").GetString());
    }

    [Fact]
    public async Task Runs_are_claimed_recovered_and_repeat_safe()
    {
        var s = await SetupAsync("auto-reliable");
        var workflow = await PostIdAsync(s.Admin, s.Workflows, new
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
                var result = await executor.ExecuteAsync(action, s.Workspace, null, null, null, null, null, "test", "test:1", executionId, Ct);
                Assert.True(result.Succeeded, result.Error);
                Assert.Equal(executionId.ToString(), result.Output!["taskId"]!.GetValue<string>());
            }
        }

        Assert.Equal(["Once"], await s.Admin.QueryTitlesAsync(s.Workspace, s.Tasks, ""));

        // A run whose start message was lost (no lease, no progress) is recovered by the timer job.
        Guid lost;
        await using (var scope = factoryScopes.CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var old = DateTimeOffset.UtcNow.AddMinutes(-10);
            lost = Guid.CreateVersion7();
            db.Runs.Add(new WorkflowRun
            {
                Id = lost,
                WorkflowId = workflow,
                WorkflowVersion = 1,
                WorkspaceId = s.Workspace,
                ListId = s.Invoices,
                ItemId = bill,
                Status = RunStatus.Running,
                StartedAt = old,
                LastActivityAt = old,
            });
            await db.SaveChangesAsync(Ct);

            // A run another handler holds is not executed twice: the message is retried later.
            var interpreter = scope.ServiceProvider.GetRequiredService<WorkflowInterpreter>();
            await db.Runs.Where(r => r.Id == lost).ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(1)), Ct);
            await Assert.ThrowsAsync<RunLeasedException>(() => interpreter.RunAsync(lost, null, Ct));
            await scope.ServiceProvider.GetRequiredService<WorkflowTimerJob>().RunAsync(Ct);
            Assert.Equal(RunStatus.Running, (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == lost, Ct)).Status);

            await db.Runs.Where(r => r.Id == lost).ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null), Ct);
            await scope.ServiceProvider.GetRequiredService<WorkflowTimerJob>().RunAsync(Ct);
        }

        var recovered = await WaitAsync(s.Admin, $"{s.Workflows}/runs/{lost}", r => Status(r) == "completed");
        Assert.Contains("Follow up Bill", await s.Admin.QueryTitlesAsync(s.Workspace, s.Tasks, ""));
        Assert.Equal(workflow, recovered.GetProperty("workflowId").GetGuid());

        // A run that keeps failing without progress is given up.
        await using (var scope = factoryScopes.CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var stuck = Guid.CreateVersion7();
            db.Runs.Add(new WorkflowRun
            {
                Id = stuck,
                WorkflowId = workflow,
                WorkflowVersion = 1,
                WorkspaceId = s.Workspace,
                ListId = s.Invoices,
                ItemId = bill,
                Status = RunStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow,
                Attempts = WorkflowInterpreter.MaxAttempts,
            });
            await db.SaveChangesAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<WorkflowInterpreter>().RunAsync(stuck, null, Ct);
            var failed = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == stuck, Ct);
            Assert.Equal(RunStatus.Failed, failed.Status);
            Assert.Contains("attempts", failed.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Flows_use_variables_and_outputs_handle_errors_and_can_be_retried()
    {
        var s = await SetupAsync("auto-flows");
        async Task<JsonElement> RunAsync(string workflow, int amount = 500)
        {
            var bill = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "Bill", amount } })).GetProperty("id").GetGuid();
            var run = await PostIdAsync(s.Admin, $"{s.Item(bill)}/workflows", new { workflow });
            return await WaitAsync(s.Admin, $"{s.Workflows}/runs/{run}", r => Status(r) is "completed" or "failed" or "waiting");
        }

        string Note(JsonElement run) =>
            GetAsync(s.Admin, s.Item(run.GetProperty("itemId").GetGuid())).Result.GetProperty("fields").GetProperty("note").GetString()!;

        // Variables, comparisons and the outputs of earlier nodes.
        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Check big bills",
            trigger = new { type = "manual", list = "Bills" },
            variables = new { limit = 100 },
            flow = new
            {
                start = "big?",
                nodes = new Dictionary<string, object>
                {
                    ["big?"] = new { activity = "if", inputs = new { left = "{amount}", op = "gt", right = "{var:limit}" }, next = new { @true = "task", @false = "small" } },
                    ["task"] = new { activity = "task.create", inputs = new { list = "Tasks", title = "Check {title}" }, next = new { done = "remember" } },
                    ["remember"] = new { activity = "setVariable", inputs = new { name = "task", value = "{step:task.taskId}" }, next = new { done = "note" } },
                    ["note"] = new { activity = "item.update", inputs = new { fields = new { note = "task {var:task}" } } },
                    ["small"] = new { activity = "item.update", inputs = new { fields = new { note = "small" } }, next = new { done = "end" } },
                    ["end"] = new { activity = "end" },
                },
            },
        });
        var big = await RunAsync("Check big bills");
        Assert.Equal("completed", Status(big));
        var taskId = big.GetProperty("outputs").GetProperty("task").GetProperty("taskId").GetString();
        Assert.Equal(taskId, big.GetProperty("variables").GetProperty("task").GetString());
        Assert.Equal(100, big.GetProperty("variables").GetProperty("limit").GetInt32());
        Assert.Equal($"task {taskId}", Note(big));
        Assert.Equal("small", Note(await RunAsync("Check big bills", amount: 50)));

        // A failing node continues on its error port with the error as its output.
        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Report failures",
            trigger = new { type = "manual", list = "Bills" },
            flow = new
            {
                start = "make",
                nodes = new Dictionary<string, object>
                {
                    ["make"] = new { activity = "task.create", inputs = new { list = "Nope", title = "x" }, next = new { error = "report" } },
                    ["report"] = new { activity = "item.update", inputs = new { fields = new { note = "failed: {step:make.error}" } } },
                },
            },
        });
        var reported = await RunAsync("Report failures");
        Assert.Equal("completed", Status(reported));
        Assert.Equal("failed: The list 'Nope' does not exist in the workspace.", Note(reported));

        // Without an error port the run fails at the node (an incident) and is retried from there once the cause is fixed.
        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Needs a list",
            trigger = new { type = "manual", list = "Bills" },
            flow = new
            {
                start = "make",
                nodes = new Dictionary<string, object>
                {
                    ["make"] = new { activity = "task.create", inputs = new { list = "Later", title = "Follow up" }, next = new { done = "stop" } },
                    ["stop"] = new { activity = "fail", inputs = new { message = "Stopped after {step:make.taskId}" } },
                },
            },
        });
        var incident = await RunAsync("Needs a list");
        Assert.Equal("failed", Status(incident));
        Assert.Equal("make", incident.GetProperty("failedNode").GetString());
        var later = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Later", templateKey = "tasks" });

        // Another organization can neither see nor retry the run.
        await factory.CreateTenantAsync("auto-flows-b");
        var foreign = await ApiClient.CreateAsync(factory, "auto-flows-b");
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{s.Workflows}/runs/{incident.GetProperty("id").GetGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync($"{s.Workflows}/runs/{incident.GetProperty("id").GetGuid()}/retry", null, Ct)).StatusCode);
        Assert.Equal("failed", Status(await GetAsync(s.Admin, $"{s.Workflows}/runs/{incident.GetProperty("id").GetGuid()}")));

        var retry = await s.Admin.PostAsync($"{s.Workflows}/runs/{incident.GetProperty("id").GetGuid()}/retry", null, Ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var stopped = await WaitAsync(s.Admin, $"{s.Workflows}/runs/{incident.GetProperty("id").GetGuid()}", r => Status(r) is "failed" && !r.TryGetProperty("failedNode", out _));
        Assert.StartsWith("Stopped after ", stopped.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(["Follow up"], await s.Admin.QueryTitlesAsync(s.Workspace, later, ""));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsync($"{s.Workflows}/runs/{stopped.GetProperty("id").GetGuid()}/retry", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsync($"{s.Workflows}/runs/{big.GetProperty("id").GetGuid()}/retry", null, Ct)).StatusCode);

        // A retry policy waits and tries the node again (the minute job resumes it).
        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Try again",
            trigger = new { type = "manual", list = "Bills" },
            flow = new
            {
                start = "make",
                nodes = new Dictionary<string, object>
                {
                    ["make"] = new { activity = "task.create", inputs = new { list = "Soon", title = "Eventually" }, retry = new { attempts = 2, delayMinutes = 0 } },
                },
            },
        });
        var waiting = await RunAsync("Try again");
        Assert.Equal("waiting", Status(waiting));
        Assert.Contains("trying again", waiting.GetProperty("log").ToString(), StringComparison.Ordinal);
        var soon = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Soon", templateKey = "tasks" });
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            await scope.ServiceProvider.GetRequiredService<WorkflowTimerJob>().RunAsync(Ct);
        }

        await WaitAsync(s.Admin, $"{s.Workflows}/runs/{waiting.GetProperty("id").GetGuid()}", r => Status(r) == "completed");
        Assert.Equal(["Eventually"], await s.Admin.QueryTitlesAsync(s.Workspace, soon, ""));

        // Flows are checked when they are saved.
        var invalid = await s.Admin.PostAsJsonAsync(s.Workflows, new
        {
            name = "Broken",
            trigger = new { type = "manual" },
            flow = new { start = "a", nodes = new Dictionary<string, object> { ["a"] = new { activity = "item.update", inputs = new { fields = new { note = "x" } }, next = new { done = "b" } } } },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("the next node 'b' (done) does not exist", await invalid.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flows_map_json_to_fields_and_loop_over_arrays_and_queries()
    {
        var s = await SetupAsync("auto-foreach");
        var lineType = await s.Admin.CreateContentTypeAsync("Line", [
            new { name = "bill", type = "lookup", lookupListId = s.Invoices },
            new { name = "qty", type = "number" },
            new { name = "note", type = "text" },
        ]);
        var lines = await s.Admin.CreateListAsync(s.Workspace, "Lines", lineType);

        // Invalid loops are rejected when saved.
        async Task<string> InvalidAsync(object flow)
        {
            var response = await s.Admin.PostAsJsonAsync(s.Workflows, new { name = "Bad loop", trigger = new { type = "manual", list = "Bills" }, flow }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        Assert.Contains("item port", await InvalidAsync(new { start = "each", nodes = new { each = new { activity = "forEach", inputs = new { items = "{var:x}" } } } }), StringComparison.Ordinal);
        Assert.Contains("either items", await InvalidAsync(new
        {
            start = "each",
            nodes = new Dictionary<string, object>
            {
                ["each"] = new { activity = "forEach", inputs = new { items = "{var:x}", query = new { list = "Lines" } }, next = new { item = "add" } },
                ["add"] = new { activity = "item.create", inputs = new { list = "Lines", fields = new { title = "x" } }, next = new { done = "each" } },
            },
        }), StringComparison.Ordinal);

        // A structured value (as an AI step returns it) becomes the bill's fields and one line per element. Lines of an
        // earlier run are found with a query and deleted first, so running again replaces them.
        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Split",
            trigger = new { type = "manual", list = "Bills" },
            variables = new { reading = new { total = 7.5, lines = new object[] { new { name = "Paper", qty = 2 }, new { name = "Ink", qty = 3.5 } } } },
            flow = new
            {
                start = "save",
                nodes = new Dictionary<string, object>
                {
                    ["save"] = new { activity = "item.update", inputs = new { fields = new { amount = "{var:reading.total}", note = "was {amount}" } }, next = new { done = "clear" } },
                    ["clear"] = new { activity = "forEach", inputs = new { query = new { list = "Lines", filter = "fields/bill eq {id}" }, @as = "old" }, next = new { item = "drop", done = "each" } },
                    ["drop"] = new { activity = "item.delete", inputs = new { list = "Lines", id = "{var:old.id}" }, next = new { done = "clear" } },
                    ["each"] = new { activity = "forEach", inputs = new { items = "{var:reading.lines}", @as = "line" }, next = new { item = "add" } },
                    ["add"] = new
                    {
                        activity = "item.create",
                        inputs = new { list = "Lines", fields = new { title = "{var:line.name}", qty = "{var:line.qty}", note = "{var:line.qty}", bill = "{id}" } },
                        next = new { done = "each" },
                    },
                },
            },
        });
        var bill = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "Bill", amount = 500 } })).GetProperty("id").GetGuid();
        async Task<JsonElement> SplitAsync()
        {
            var run = await PostIdAsync(s.Admin, $"{s.Item(bill)}/workflows", new { workflow = "Split" });
            var done = await WaitAsync(s.Admin, $"{s.Workflows}/runs/{run}", r => Status(r) is "completed" or "failed");
            Assert.True(Status(done) == "completed", done.ToString());
            return done;
        }

        var first = await SplitAsync();
        Assert.Equal(2, first.GetProperty("outputs").GetProperty("each").GetProperty("count").GetInt32());
        Assert.False(first.GetProperty("variables").TryGetProperty("line", out _));
        var fields = (await GetAsync(s.Admin, s.Item(bill))).GetProperty("fields");
        Assert.Equal(7.5m, fields.GetProperty("amount").GetDecimal());
        Assert.Equal("was 500", fields.GetProperty("note").GetString());
        var created = Values(await GetAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists/{lines}/items")).Select(i => i.GetProperty("fields")).OrderBy(f => f.GetProperty("title").GetString()).ToList();
        Assert.Equal(["Ink", "Paper"], created.Select(f => f.GetProperty("title").GetString()));
        Assert.Equal([3.5m, 2m], created.Select(f => f.GetProperty("qty").GetDecimal()));
        Assert.Equal(["3.5", "2"], created.Select(f => f.GetProperty("note").GetString())); // a number into a text field is its text
        Assert.All(created, f => Assert.Equal(bill.ToString(), f.GetProperty("bill").GetString()));

        var firstIds = Values(await GetAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists/{lines}/items")).Select(i => i.GetProperty("id").GetGuid()).ToList();
        await SplitAsync();
        var again = Values(await GetAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists/{lines}/items")).Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, again.Count);
        Assert.Empty(again.Intersect(firstIds));

        // A forEach over something that is not a list fails at the node.
        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Not a list",
            trigger = new { type = "manual", list = "Bills" },
            flow = new
            {
                start = "each",
                nodes = new Dictionary<string, object>
                {
                    ["each"] = new { activity = "forEach", inputs = new { items = "{amount}" }, next = new { item = "noop" } },
                    ["noop"] = new { activity = "setVariable", inputs = new { name = "x", value = 1 }, next = new { done = "each" } },
                },
            },
        });
        var notList = await PostIdAsync(s.Admin, $"{s.Item(bill)}/workflows", new { workflow = "Not a list" });
        var failed = await WaitAsync(s.Admin, $"{s.Workflows}/runs/{notList}", r => Status(r) is "completed" or "failed");
        Assert.Equal("failed", Status(failed));
        Assert.Contains("items is not a list", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extension_triggers_start_workflows_that_use_extension_actions()
    {
        var s = await SetupAsync("auto-ext");
        Assert.True((await s.Admin.PostAsync("/v1.0/extensions/samples.invoices/enable", null, Ct)).IsSuccessStatusCode);
        var invoices = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Invoices", templateKey = "samples.invoices.invoices" });
        var workflow = await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Large invoices",
            trigger = new { type = "samples.invoices.approvalNeeded" },
            steps = new object[]
            {
                new { type = "action", action = "samples.invoices.approve" },
                Action("item.update", new { fields = new { title = "Approved ({data:amount})" } }),
            },
        });

        var actions = await GetAsync(s.Admin, "/v1.0/workflows/activities");
        Assert.Contains(actions.EnumerateArray(), a => a.GetProperty("key").GetString() == "samples.invoices.approve");
        Assert.Contains((await GetAsync(s.Admin, "/v1.0/workflows/triggers")).EnumerateArray(), t => t.GetProperty("key").GetString() == "samples.invoices.approvalNeeded");

        var invoice = (await s.Admin.CreateItemAsync(s.Workspace, invoices, new { fields = new { title = "Big", amount = 5000 } })).GetProperty("id").GetGuid();
        var approved = await WaitAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists/{invoices}/items/{invoice}", i => i.GetProperty("fields").GetProperty("status").GetString() == "approved"
            && i.GetProperty("fields").GetProperty("title").GetString()!.StartsWith("Approved", StringComparison.Ordinal));
        Assert.Equal("Approved (5000)", approved.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("completed", Status(Values(await GetAsync(s.Admin, $"{s.Workflows}/runs?workflowId={workflow}")).Single()));
    }

    [Fact]
    public async Task Activities_wait_until_other_modules_complete_them_or_time_out()
    {
        var s = await SetupAsync("auto-wait");
        Assert.True((await s.Admin.PostAsync("/v1.0/extensions/samples.invoices/enable", null, Ct)).IsSuccessStatusCode);
        var invoices = await PostIdAsync(s.Admin, $"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Invoices", templateKey = "samples.invoices.invoices" });
        string Item(Guid id) => $"/v1.0/workspaces/{s.Workspace}/lists/{invoices}/items/{id}";

        // The catalog lists flow activities and actions with their ports and input schemas.
        var catalog = (await GetAsync(s.Admin, "/v1.0/workflows/activities")).EnumerateArray().ToList();
        var awaitPayment = catalog.Single(a => a.GetProperty("key").GetString() == "samples.invoices.awaitPayment");
        Assert.Equal("action", awaitPayment.GetProperty("kind").GetString());
        Assert.Equal(["done", "error", "paid", "timeout"], awaitPayment.GetProperty("ports").EnumerateArray().Select(p => p.GetString()));
        Assert.True(awaitPayment.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("days", out _));
        Assert.Equal("flow", catalog.Single(a => a.GetProperty("key").GetString() == "if").GetProperty("kind").GetString());
        Assert.Contains("list", catalog.Single(a => a.GetProperty("key").GetString() == "task.create").GetProperty("inputSchema").GetProperty("required").ToString(), StringComparison.Ordinal);

        await PostIdAsync(s.Admin, s.Workflows, new
        {
            name = "Chase payment",
            trigger = new { type = "manual", list = "Invoices" },
            flow = new
            {
                start = "wait",
                nodes = new Dictionary<string, object>
                {
                    ["wait"] = new { activity = "samples.invoices.awaitPayment", inputs = new { days = 14 }, next = new { paid = "thanks", timeout = "chase" } },
                    ["thanks"] = new { activity = "item.update", inputs = new { fields = new { title = "Paid" } } },
                    ["chase"] = new { activity = "item.update", inputs = new { fields = new { title = "Overdue" } } },
                },
            },
        });
        async Task<(Guid Invoice, Guid Run)> StartAsync()
        {
            var invoice = (await s.Admin.CreateItemAsync(s.Workspace, invoices, new { fields = new { title = "Invoice", amount = 10 } })).GetProperty("id").GetGuid();
            return (invoice, await PostIdAsync(s.Admin, $"{Item(invoice)}/workflows", new { workflow = "Chase payment" }));
        }

        // The run waits until the invoice is marked paid; the extension completes the wait from its event subscriber.
        var (paid, paidRun) = await StartAsync();
        await WaitAsync(s.Admin, $"{s.Workflows}/runs/{paidRun}", r => Status(r) == "waiting");
        var update = await s.Admin.SendWithEtagAsync(HttpMethod.Patch, Item(paid), (await s.Admin.GetAsync(Item(paid), Ct)).Headers.ETag!.Tag, new { fields = new { status = "paid" } });
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync(Ct));
        var done = await WaitAsync(s.Admin, $"{s.Workflows}/runs/{paidRun}", r => Status(r) == "completed");
        Assert.Equal("paid", done.GetProperty("outputs").GetProperty("wait").GetProperty("outcome").GetString());
        Assert.Equal("Paid", (await GetAsync(s.Admin, Item(paid))).GetProperty("fields").GetProperty("title").GetString());

        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier);
        var bookmarks = scope.ServiceProvider.GetRequiredService<PaperDotNet.Workflows.Contracts.IWorkflowBookmarks>();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();

        // Completing again changes nothing; the engine's own waits cannot be completed from outside.
        Assert.False(await bookmarks.CompleteAsync("samples.invoices.payment", paid.ToString("N"), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => bookmarks.CompleteAsync("approval", "x", null, Ct));

        // A completion that arrives before the run waits is kept, and the run continues right away when it gets there.
        var early = (await s.Admin.CreateItemAsync(s.Workspace, invoices, new { fields = new { title = "Invoice", amount = 10 } })).GetProperty("id").GetGuid();
        Assert.True(await bookmarks.CompleteAsync("samples.invoices.payment", early.ToString("N"), new System.Text.Json.Nodes.JsonObject { ["outcome"] = "paid" }, Ct));
        var earlyRun = await PostIdAsync(s.Admin, $"{Item(early)}/workflows", new { workflow = "Chase payment" });
        await WaitAsync(s.Admin, $"{s.Workflows}/runs/{earlyRun}", r => Status(r) == "completed");
        Assert.Equal("Paid", (await GetAsync(s.Admin, Item(early))).GetProperty("fields").GetProperty("title").GetString());

        // Without a payment the wait times out (the minute job) and the run continues on the timeout port.
        var (unpaid, unpaidRun) = await StartAsync();
        await WaitAsync(s.Admin, $"{s.Workflows}/runs/{unpaidRun}", r => Status(r) == "waiting");
        var wait = await db.Bookmarks.SingleAsync(b => b.RunId == unpaidRun, Ct);
        Assert.Equal("samples.invoices.payment", wait.Kind);
        Assert.True(wait.ResumeAt > DateTimeOffset.UtcNow.AddDays(13));
        wait.ResumeAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<WorkflowTimerJob>().RunAsync(Ct);
        await WaitAsync(s.Admin, $"{s.Workflows}/runs/{unpaidRun}", r => Status(r) == "completed");
        Assert.Equal("Overdue", (await GetAsync(s.Admin, Item(unpaid))).GetProperty("fields").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Workflow_is_validated_needs_access_and_stays_in_the_tenant()
    {
        var s = await SetupAsync("auto-acl");
        await factory.CreateTenantAsync("auto-acl-b");
        var foreign = await ApiClient.CreateAsync(factory, "auto-acl-b");
        var carol = await ApiClient.CreateAsync(factory, "auto-acl", "carol", "carol-password-1");
        var notify = new[] { Action("notify", new { to = new[] { "alice" }, title = "t" }) };

        async Task<string> InvalidAsync(object workflow)
        {
            var response = await s.Admin.PostAsJsonAsync(s.Workflows, workflow, Ct);
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
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.PostAsJsonAsync(s.Workflows, valid, Ct)).StatusCode);
        var workflow = await PostIdAsync(s.Admin, s.Workflows, valid);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync(s.Workflows, valid, Ct)).StatusCode);
        Assert.Single((await GetAsync(carol, s.Workflows)).EnumerateArray());

        // Only manual workflows are started by people, and only where their trigger allows.
        var bill = (await s.Admin.CreateItemAsync(s.Workspace, s.Invoices, new { fields = new { title = "Bill", amount = 1 } })).GetProperty("id").GetGuid();
        async Task<string> StartFailsAsync(HttpClient client, string name)
        {
            var response = await client.PostAsJsonAsync($"{s.Item(bill)}/workflows", new { workflow = name }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        Assert.Contains("not started manually", await StartFailsAsync(s.Admin, "Notify"), StringComparison.Ordinal);
        await PostIdAsync(s.Admin, s.Workflows, new { name = "Tasks only", trigger = new { type = "manual", list = "Tasks" }, steps = notify });
        Assert.Contains("only runs on items of the list", await StartFailsAsync(s.Admin, "Tasks only"), StringComparison.Ordinal);
        await PostIdAsync(s.Admin, s.Workflows, new { name = "Big only", trigger = new { type = "manual", list = "Bills" }, condition = "fields/amount gt 100", steps = notify });
        Assert.Contains("does not match", await StartFailsAsync(s.Admin, "Big only"), StringComparison.Ordinal);
        Assert.Contains("does not match", await StartFailsAsync(carol, "Big only"), StringComparison.Ordinal); // members may start them

        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(s.Workflows, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{s.Workflows}/{workflow}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync(s.Workflows, new { name = "w", trigger = new { type = "manual" }, steps = new object[] { new { type = "delay", hours = 1 } } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"{s.Workflows}/runs", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"{s.Item(bill)}/workflows", new { workflow = "Big only" }, Ct)).StatusCode);
        Assert.Empty(Values(await GetAsync(foreign, "/v1.0/me/approvals")));

        // Workflows travel with workspace templates (by name) and apply idempotently.
        var xml = await (await s.Admin.GetAsync($"/v1.0/provisioning/export?workspaceId={s.Workspace}", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("urn:paperdotnet:workflow:1", xml, StringComparison.Ordinal);
        var apply = await foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.True(apply.IsSuccessStatusCode, await apply.Content.ReadAsStringAsync(Ct));
        var foreignWs = Values(await GetAsync(foreign, "/v1.0/workspaces")).Single(w => w.GetProperty("name").GetString() == "Finance").GetProperty("id").GetGuid();
        Assert.Equal(["Big only", "Notify", "Tasks only"], (await GetAsync(foreign, $"/v1.0/workspaces/{foreignWs}/workflows")).EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        var again = await foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"), Ct);
        Assert.Empty((await again.ReadJsonAsync()).GetProperty("changes").EnumerateArray());

        // Templates made before the rename (section Automations in urn:paperdotnet:automation:2) still apply.
        var legacy = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace current = "urn:paperdotnet:workflow:1", old = "urn:paperdotnet:automation:2";
        foreach (var element in legacy.Descendants().Where(e => e.Name.Namespace == current).ToList())
        {
            element.Name = old + (element.Name.LocalName == "Workflows" ? "Automations" : "Automation");
            element.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == current.NamespaceName).ToList().ForEach(a => a.Value = old.NamespaceName);
        }

        legacy.Descendants(old + "Automation").Single(e => (string?)e.Attribute("Name") == "Notify").SetAttributeValue("Enabled", "false");
        var legacyApply = await foreign.PostAsync("/v1.0/provisioning/apply", new StringContent(legacy.ToString(), System.Text.Encoding.UTF8, "application/xml"), Ct);
        var legacyResult = await legacyApply.ReadJsonAsync();
        Assert.True(legacyApply.IsSuccessStatusCode, legacyResult.ToString());
        Assert.Contains(legacyResult.GetProperty("changes").EnumerateArray(), c => c.ToString().Contains("Notify", StringComparison.Ordinal));
        Assert.False((await GetAsync(foreign, $"/v1.0/workspaces/{foreignWs}/workflows")).EnumerateArray().Single(w => w.GetProperty("name").GetString() == "Notify").GetProperty("enabled").GetBoolean());
    }
}
