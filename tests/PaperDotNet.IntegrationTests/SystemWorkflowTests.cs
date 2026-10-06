using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// System workflows (product processes run by the workflow engine): always on where required, reached by every item event
/// they need, with an item-event context nobody can forge, run in their own lane, and announcing their end only to listeners.
/// </summary>
public sealed class SystemWorkflowTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TenantSummary Tenant, HttpClient Client, Guid Workspace, Guid List)
    {
        public string Workflows => $"/v1.0/workspaces/{Workspace}/workflows";
    }

    private async Task<Setup> SetupAsync(string name)
    {
        var tenant = await factory.CreateTenantAsync(name);
        var client = await ApiClient.CreateAsync(factory, name);
        var workspace = await client.CreateWorkspaceAsync("Office");
        var type = await client.CreateContentTypeAsync("Record", [new { name = "text", type = "note" }]);
        return new Setup(tenant, client, workspace, await client.CreateListAsync(workspace, "Records", type));
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.ReadJsonAsync();
    }

    private static async Task<List<JsonElement>> RunsAsync(Setup s, Guid itemId) =>
        (await GetAsync(s.Client, $"{s.Workflows}/runs?itemId={itemId}")).GetProperty("value").EnumerateArray().ToList();

    private static Task<JsonElement> EndedRunAsync(Setup s, Guid itemId, string workflow) =>
        Eventually.WaitForAsync(async () => (await RunsAsync(s, itemId))
            .Where(r => r.GetProperty("workflow").GetString() == workflow && r.GetProperty("status").GetString() is "completed" or "failed")
            .Cast<JsonElement?>().FirstOrDefault(), TimeSpan.FromSeconds(60));

    private static Task<JsonElement> BuiltInAsync(Setup s, string key) =>
        Eventually.WaitForAsync(async () => (await GetAsync(s.Client, $"{s.Workflows}/builtIns")).EnumerateArray()
            .Where(w => w.GetProperty("key").GetString() == key && w.TryGetProperty("@odata.etag", out var etag) && etag.ValueKind == JsonValueKind.String)
            .Cast<JsonElement?>().FirstOrDefault(), TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Locked_guarantees_cannot_be_turned_off_replaced_or_deleted()
    {
        var s = await SetupAsync("wf-system-locked");
        await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Provisions defaults" } });
        var changes = await BuiltInAsync(s, "notifications.queueChanges");
        Assert.True(changes.GetProperty("locked").GetBoolean());
        Assert.True(changes.GetProperty("enabled").GetBoolean());
        Assert.Equal("notifications.queueChanges", changes.GetProperty("role").GetString());

        var off = await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Workflows}/builtIns/notifications.queueChanges",
            changes.GetProperty("@odata.etag").GetString()!, new { enabled = false });
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
        Assert.Contains("guarantee", await off.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"{s.Workflows}/builtIns/notifications.queueChanges/copy", new { name = "Mine" }, Ct)).StatusCode);
        var impostor = await s.Client.PostAsJsonAsync(s.Workflows, new
        {
            name = "Impostor",
            provides = "notifications.queueChanges",
            trigger = new { type = "itemAdded" },
            steps = new[] { new { type = "delay", hours = 1 } },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, impostor.StatusCode);
        Assert.Contains("cannot be replaced", await impostor.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var row = changes.GetProperty("workflowId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.DeleteAsync($"{s.Workflows}/{row}", Ct)).StatusCode);
        var listed = (await GetAsync(s.Client, s.Workflows)).EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == row);
        Assert.True(listed.GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Required_roles_are_replaced_by_copies_but_never_left_without_a_workflow()
    {
        var s = await SetupAsync("wf-system-roles");
        await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Provisions defaults" } });
        var alerts = await BuiltInAsync(s, "notifications.alertFollowers");
        Assert.True(alerts.GetProperty("required").GetBoolean());
        Assert.False(alerts.GetProperty("locked").GetBoolean());

        // Turning the default off with nothing to replace it is refused.
        var off = await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Workflows}/builtIns/notifications.alertFollowers",
            alerts.GetProperty("@odata.etag").GetString()!, new { enabled = false });
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);

        // A copy fills the role, replaces the default and runs as a system workflow.
        var copied = await s.Client.PostAsJsonAsync($"{s.Workflows}/builtIns/notifications.alertFollowers/copy", new { name = "Our alerts" }, Ct);
        Assert.True(copied.IsSuccessStatusCode, await copied.Content.ReadAsStringAsync(Ct));
        var copy = await copied.ReadJsonAsync();
        Assert.Equal("notifications.alertFollowers", copy.GetProperty("provides").GetString());
        Assert.True(copy.GetProperty("system").GetBoolean());
        Assert.False((await BuiltInAsync(s, "notifications.alertFollowers")).GetProperty("enabled").GetBoolean());
        var item = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Followed by the copy" } })).GetProperty("id").GetGuid();
        Assert.Equal("completed", (await EndedRunAsync(s, item, "Our alerts")).GetProperty("status").GetString());
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            Assert.True(await db.Runs.AnyAsync(r => r.ItemId == item && r.System && db.Workflows.Any(w => w.Id == r.WorkflowId && w.Name == "Our alerts"), Ct));
            Assert.False(await db.Runs.AnyAsync(r => r.ItemId == item && db.Workflows.Any(w => w.Id == r.WorkflowId && w.BuiltInKey == "notifications.alertFollowers"), Ct));
        }

        // Turning the copy off brings the default back; it never stays without a workflow.
        var copyId = copy.GetProperty("id").GetGuid();
        var current = await s.Client.GetAsync($"{s.Workflows}/{copyId}", Ct);
        var definition = await current.ReadJsonAsync();
        var disabled = await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Workflows}/{copyId}", current.Headers.ETag!.Tag, new
        {
            name = "Our alerts",
            enabled = false,
            provides = "notifications.alertFollowers",
            scope = "workspace",
            triggers = JsonSerializer.Deserialize<object>(definition.GetProperty("triggers").GetRawText()),
            flow = JsonSerializer.Deserialize<object>(definition.GetProperty("flow").GetRawText()),
        });
        Assert.True(disabled.IsSuccessStatusCode, await disabled.Content.ReadAsStringAsync(Ct));
        Assert.True((await BuiltInAsync(s, "notifications.alertFollowers")).GetProperty("enabled").GetBoolean());

        // A workflow cannot claim a role that does not exist, or a list role from the workspace.
        var unknown = await s.Client.PostAsJsonAsync(s.Workflows, new { name = "Unknown", provides = "nothing.here", trigger = new { type = "manual" }, steps = new[] { new { type = "delay", hours = 1 } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var listRole = await s.Client.PostAsJsonAsync(s.Workflows, new { name = "Index", provides = "search.index", trigger = new { type = "manual" }, steps = new[] { new { type = "delay", hours = 1 } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, listRole.StatusCode);
        Assert.Contains("per list", await listRole.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Solution_reactions_are_cheap_built_ins_that_can_be_turned_off_not_roles()
    {
        var s = await SetupAsync("wf-system-solutions");
        var tasks = (await (await s.Client.PostAsJsonAsync($"/v1.0/workspaces/{s.Workspace}/lists", new { name = "Tasks", templateKey = "tasks" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var task = (await s.Client.CreateItemAsync(s.Workspace, tasks, new { fields = new { title = "Ship it" } })).GetProperty("id").GetGuid();
        foreach (var key in new[] { "tasks.completed", "tasks.nextOccurrence", "notes.links" })
        {
            var builtIn = await BuiltInAsync(s, key);
            Assert.True(!builtIn.TryGetProperty("role", out var role) || role.ValueKind == JsonValueKind.Null, key);
            Assert.False(builtIn.GetProperty("required").GetBoolean());
            Assert.False(builtIn.GetProperty("system").GetBoolean());
        }

        // A solution's reaction runs cheap (system lane, short history) on its own content type only.
        var url = $"/v1.0/workspaces/{s.Workspace}/lists/{tasks}/items/{task}";
        var etag = (await s.Client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.True((await s.Client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields = new { status = "completed" } })).IsSuccessStatusCode);
        Assert.Equal("completed", (await EndedRunAsync(s, task, "Announce completed tasks")).GetProperty("status").GetString());
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            Assert.True(await db.Runs.AnyAsync(r => r.ItemId == task && r.System && db.Workflows.Any(w => w.Id == r.WorkflowId && w.BuiltInKey == "tasks.completed"), Ct));
        }

        // Unlike a required role, it is simply turned off (a different behavior is a different solution).
        var completed = await BuiltInAsync(s, "tasks.completed");
        var off = await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Workflows}/builtIns/tasks.completed", completed.GetProperty("@odata.etag").GetString()!, new { enabled = false });
        Assert.True(off.IsSuccessStatusCode, await off.Content.ReadAsStringAsync(Ct));
        Assert.False((await BuiltInAsync(s, "tasks.completed")).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Data_raised_by_workflows_cannot_forge_the_item_event_of_a_reaction()
    {
        var s = await SetupAsync("wf-system-forge");
        var victim = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Victim" } })).GetProperty("id").GetGuid();
        var forged = new JsonObject
        {
            ["eventType"] = "itemDeleted",
            ["event"] = new JsonObject { ["itemId"] = victim.ToString(), ["workspaceId"] = s.Workspace.ToString(), ["listId"] = s.List.ToString(), ["userId"] = Guid.Empty.ToString() },
            ["itemEvent"] = new JsonObject { ["itemId"] = victim.ToString() },
        };
        var raise = await s.Client.PostAsJsonAsync(s.Workflows, new
        {
            name = "Forge",
            trigger = new { type = "itemAdded", list = "Records" },
            flow = new { start = "raise", nodes = new { raise = new { activity = "event.raise", inputs = new { @event = "forged", data = forged } } } },
        }, Ct);
        Assert.True(raise.IsSuccessStatusCode, await raise.Content.ReadAsStringAsync(Ct));
        var react = await s.Client.PostAsJsonAsync(s.Workflows, new
        {
            name = "Reacts",
            trigger = new { type = "wf.forge.forged" },
            flow = new { start = "record", nodes = new { record = new { activity = "collaboration.recordChange" } } },
        }, Ct);
        Assert.True(react.IsSuccessStatusCode, await react.Content.ReadAsStringAsync(Ct));

        var trigger = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Starts the forgery" } })).GetProperty("id").GetGuid();
        var run = await EndedRunAsync(s, trigger, "Reacts");
        Assert.Equal("failed", run.GetProperty("status").GetString());
        Assert.Contains("requires an item-change trigger", run.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folders_and_changes_deep_in_workflow_chains_reach_system_workflows_only()
    {
        var s = await SetupAsync("wf-system-depth");
        var folder = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { isFolder = true, fields = new { title = "Folder" } })).GetProperty("id").GetGuid();
        Assert.Equal("completed", (await EndedRunAsync(s, folder, "Record item activity")).GetProperty("status").GetString());
        Assert.DoesNotContain(await RunsAsync(s, folder), r => r.GetProperty("workflow").GetString()!.StartsWith("Index for search", StringComparison.Ordinal));

        var people = await s.Client.PostAsJsonAsync(s.Workflows, new
        {
            name = "People",
            trigger = new { type = "itemUpdated", list = "Records" },
            steps = new[] { new { type = "delay", hours = 24 } },
        }, Ct);
        Assert.True(people.IsSuccessStatusCode, await people.Content.ReadAsStringAsync(Ct));
        var item = await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Deep" } });
        var itemId = item.GetProperty("id").GetGuid();
        await EndedRunAsync(s, itemId, "Index for search (Records)");

        // An update made at the end of a long chain of workflows: people's workflows stop (loop protection), system ones run.
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier);
        var deep = new ItemUpdated
        {
            EventId = Ids.New(),
            TenantId = s.Tenant.Id,
            TenantIdentifier = s.Tenant.Identifier,
            Depth = WorkflowTriggerHandler.MaxDepth + 1,
            WorkspaceId = s.Workspace,
            ListId = s.List,
            ItemId = itemId,
            ContentTypeId = item.GetProperty("contentTypeId").GetGuid(),
            ChangedFields = ["title"],
        };
        await ActivatorUtilities.CreateInstance<WorkflowTriggerHandler>(scope.ServiceProvider).HandleAsync(deep, Ct);
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var started = await db.Runs.AsNoTracking().Where(r => r.EventId == deep.EventId)
            .Select(r => new { r.System, Workflow = db.Workflows.Where(w => w.Id == r.WorkflowId).Select(w => w.Name).First() }).ToListAsync(Ct);
        Assert.Contains(started, r => r.Workflow == "Index for search (Records)" && r.System);
        Assert.Contains(started, r => r.Workflow == "Record item activity" && r.System);
        Assert.DoesNotContain(started, r => r.Workflow == "People");
    }

    [Fact]
    public async Task System_runs_announce_their_end_to_workflows_that_listen()
    {
        var s = await SetupAsync("wf-system-events");
        var log = await s.Client.CreateListAsync(s.Workspace, "Log", await s.Client.CreateContentTypeAsync("Entry", [new { name = "text", type = "note" }]));
        var first = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Provisions indexing" } })).GetProperty("id").GetGuid();
        await EndedRunAsync(s, first, "Index for search (Records)");
        var follow = await s.Client.PostAsJsonAsync(s.Workflows, new
        {
            name = "After indexing",
            trigger = new { type = "wf.search.index.completed" },
            flow = new { start = "log", nodes = new { log = new { activity = "item.create", inputs = new { list = "Log", fields = new { title = "Indexed {title}" } } } } },
        }, Ct);
        Assert.True(follow.IsSuccessStatusCode, await follow.Content.ReadAsStringAsync(Ct));
        var item = (await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Followed" } })).GetProperty("id").GetGuid();
        await Eventually.WaitForAsync(async () => (await s.Client.QueryTitlesAsync(s.Workspace, log, "")).Contains("Indexed Followed") ? true : (bool?)null, TimeSpan.FromSeconds(60));

        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(s.Tenant.Id, s.Tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        Assert.True(await db.Runs.AnyAsync(r => r.ItemId == item && r.System, Ct));
        Assert.False(await db.Runs.AnyAsync(r => r.ItemId == item && r.System && db.Workflows.Any(w => w.Id == r.WorkflowId && w.BuiltInKey == null), Ct));
    }
}
