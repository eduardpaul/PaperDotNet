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

    [Fact]
    public async Task Required_system_workflows_are_always_on_and_cannot_be_turned_off_copied_or_deleted()
    {
        var s = await SetupAsync("wf-system-required");
        await s.Client.CreateItemAsync(s.Workspace, s.List, new { fields = new { title = "Provisions defaults" } });
        var timeline = await Eventually.WaitForAsync(async () => (await GetAsync(s.Client, $"{s.Workflows}/builtIns")).EnumerateArray()
            .Where(w => w.GetProperty("key").GetString() == "collaboration.recordChange" && w.TryGetProperty("@odata.etag", out var etag) && etag.ValueKind == JsonValueKind.String)
            .Cast<JsonElement?>().FirstOrDefault(), TimeSpan.FromSeconds(30));
        Assert.True(timeline.GetProperty("required").GetBoolean());
        Assert.True(timeline.GetProperty("system").GetBoolean());
        Assert.True(timeline.GetProperty("enabled").GetBoolean());

        var off = await s.Client.SendWithEtagAsync(HttpMethod.Put, $"{s.Workflows}/builtIns/collaboration.recordChange",
            timeline.GetProperty("@odata.etag").GetString()!, new { enabled = false });
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
        Assert.Contains("cannot be turned off", await off.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.False((await s.Client.PostAsJsonAsync($"{s.Workflows}/builtIns/collaboration.recordChange/copy", new { name = "My timeline" }, Ct)).IsSuccessStatusCode);
        var row = timeline.GetProperty("workflowId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.DeleteAsync($"{s.Workflows}/{row}", Ct)).StatusCode);

        // The workspace's workflows show which ones belong to the product.
        var listed = (await GetAsync(s.Client, s.Workflows)).EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == row);
        Assert.True(listed.GetProperty("required").GetBoolean());
        Assert.True(listed.GetProperty("enabled").GetBoolean());
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
