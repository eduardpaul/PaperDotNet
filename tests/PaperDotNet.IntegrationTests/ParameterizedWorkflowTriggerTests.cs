using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

public sealed class ParameterizedWorkflowTriggerTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Snapshot_conditions_handle_out_of_order_events_missing_fields_and_legacy_payloads()
    {
        var tenant = await factory.CreateTenantAsync("snapshot-triggers");
        var client = await ApiClient.CreateAsync(factory, tenant.Identifier);
        var workspace = await client.CreateWorkspaceAsync("Snapshots");
        async Task<Guid> Create(string url, object body)
        {
            var response = await client.PostAsJsonAsync(url, body, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        }
        var list = await Create($"/v1.0/workspaces/{workspace}/lists", new { name = "Tasks", templateKey = "tasks" });
        var item = await client.CreateItemAsync(workspace, list, new { fields = new { title = "Current value differs from both events" } });
        var itemId = item.GetProperty("id").GetGuid();
        var contentType = item.GetProperty("contentTypeId").GetGuid();
        var root = $"/v1.0/workspaces/{workspace}/workflows";
        var parameters = JsonNode.Parse("""{"when":{"all":[{"target":"field","field":"title","operator":"transition","from":"Draft","to":"Ready"},{"target":"field","field":"amount","operator":"gt","value":5}]}}""");
        var workflow = await Create(root, new
        {
            name = "Ready event",
            scope = "workspace",
            triggers = new[] { new { type = "itemUpdated", parameters }, new { type = "itemUpdated", parameters } },
            steps = new[] { new { type = "delay", hours = 24 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root, new
        {
            name = "Wrong field",
            scope = "list",
            trigger = new { type = "itemUpdated", list = "Tasks", parameters },
            steps = new[] { new { type = "delay", hours = 24 } },
        }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root, new
        {
            name = "Removed trigger",
            scope = "workspace",
            trigger = new { type = "tagAdded" },
            steps = new[] { new { type = "delay", hours = 24 } },
        }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root, new
        {
            name = "Unknown term",
            scope = "workspace",
            trigger = new { type = "itemUpdated", parameters = new { when = new { target = "tags", @operator = "added", term = "Missing/Set/Term" } } },
            steps = new[] { new { type = "delay", hours = 24 } },
        }, Ct)).StatusCode);
        var scopes = factory.Services.GetRequiredService<ITenantScopeFactory>();
        await using var scope = scopes.CreateScope(tenant.Id, tenant.Identifier);
        var handler = scope.ServiceProvider.GetRequiredService<WorkflowTriggerHandler>();
        ItemSnapshot Snapshot(string title, bool hasAmount = true, string amountType = "number") => new(itemId, workspace, list, "Tasks", contentType, "Task", "task", null, false,
            new JsonObject { ["title"] = title, ["amount"] = 10 }, hasAmount
                ? new Dictionary<string, ItemSnapshotField> { ["title"] = new("text", false), ["amount"] = new(amountType, false) }
                : new Dictionary<string, ItemSnapshotField> { ["title"] = new("text", false) });
        ItemUpdated Event(ItemSnapshot? before, ItemSnapshot? after) => new()
        {
            TenantId = tenant.Id,
            TenantIdentifier = tenant.Identifier,
            WorkspaceId = workspace,
            ListId = list,
            ItemId = itemId,
            ContentTypeId = contentType,
            Before = before,
            After = after,
            ChangedFields = ["title"],
        };
        await handler.HandleAsync(Event(Snapshot("Ready"), Snapshot("Later")), Ct);
        var earlier = Event(Snapshot("Draft"), Snapshot("Ready"));
        await handler.HandleAsync(earlier, Ct);
        await handler.HandleAsync(earlier, Ct); // matching two triggers and redelivery still starts just one run
        await handler.HandleAsync(Event(Snapshot("Draft"), Snapshot("Ready", false)), Ct);
        await handler.HandleAsync(Event(Snapshot("Draft"), Snapshot("Ready", amountType: "text")), Ct);
        await handler.HandleAsync(Event(null, null), Ct); // no live-value fallback
        var runs = await (await client.GetAsync($"{root}/runs?workflowId={workflow}", Ct)).ReadJsonAsync();
        var run = Assert.Single(runs.GetProperty("value").EnumerateArray());
        var data = run.GetProperty("executionContext").GetProperty("data");
        Assert.Equal("Draft", data.GetProperty("before").GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("Ready", data.GetProperty("after").GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal("title", data.GetProperty("changedFields")[0].GetString());
    }

    [Fact]
    public async Task Taxonomy_and_relationship_updates_capture_typed_snapshots()
    {
        await factory.CreateTenantAsync("producer-snapshots");
        var client = await ApiClient.CreateAsync(factory, "producer-snapshots");
        var workspace = await client.CreateWorkspaceAsync("Producers");
        async Task<Guid> Create(string url, object body)
        {
            var response = await client.PostAsJsonAsync(url, body, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        }
        var group = await Create("/v1.0/termStore/groups", new { name = "Documents" });
        var set = await Create("/v1.0/termStore/sets", new { groupId = group, name = "Tags" });
        var source = await Create($"/v1.0/termStore/sets/{set}/terms", new { name = "Source" });
        var target = await Create($"/v1.0/termStore/sets/{set}/terms", new { name = "Target" });
        var contentType = await client.CreateContentTypeAsync("Paper", [new { name = "tags", type = "managedMetadata", termSetId = set, allowMultiple = true }]);
        var list = await client.CreateListAsync(workspace, "Papers", contentType);
        var a = await client.CreateItemAsync(workspace, list, new { fields = new { title = "A", tags = new[] { source } } });
        var b = await client.CreateItemAsync(workspace, list, new { fields = new { title = "B" } });
        var root = $"/v1.0/workspaces/{workspace}/workflows";
        var workflow = await Create(root, new
        {
            name = "Producer snapshots",
            scope = "workspace",
            trigger = new { type = "itemUpdated" },
            steps = new[] { new { type = "delay", hours = 24 } },
        });
        (await client.PostAsJsonAsync($"/v1.0/termStore/sets/{set}/terms/{source}/merge", new { targetTermId = target }, Ct)).EnsureSuccessStatusCode();
        async Task<JsonElement?> Runs(int count)
        {
            var page = await (await client.GetAsync($"{root}/runs?workflowId={workflow}", Ct)).ReadJsonAsync();
            var values = page.GetProperty("value").EnumerateArray().ToList();
            return values.Count >= count ? page : (JsonElement?)null;
        }
        var mergedPage = await Eventually.WaitForAsync(() => Runs(1), TimeSpan.FromSeconds(30));
        var merged = Assert.Single(mergedPage.GetProperty("value").EnumerateArray());
        var data = merged.GetProperty("executionContext").GetProperty("data");
        Assert.Equal(source, data.GetProperty("before").GetProperty("fields").GetProperty("tags")[0].GetGuid());
        Assert.Equal(target, data.GetProperty("after").GetProperty("fields").GetProperty("tags")[0].GetGuid());
        Assert.Equal("managedMetadata", data.GetProperty("after").GetProperty("fieldTypes").GetProperty("tags").GetProperty("type").GetString());
        (await client.PostAsJsonAsync($"/v1.0/items/{a.GetProperty("id").GetGuid()}/relationships", new { otherId = b.GetProperty("id").GetGuid() }, Ct)).EnsureSuccessStatusCode();
        var related = await Eventually.WaitForAsync(() => Runs(3), TimeSpan.FromSeconds(30));
        foreach (var run in related.GetProperty("value").EnumerateArray().Where(run => run.GetProperty("id").GetGuid() != merged.GetProperty("id").GetGuid()))
        {
            var graph = run.GetProperty("executionContext").GetProperty("data");
            Assert.True(graph.TryGetProperty("before", out _));
            Assert.True(graph.TryGetProperty("after", out _));
            Assert.Equal("relatedItems", graph.GetProperty("changedFields")[0].GetString());
        }
    }

    [Fact]
    public async Task Data_migration_disables_retired_triggers_without_changing_versions_or_other_workflows()
    {
        var tenant = await factory.CreateTenantAsync("retired-trigger-migration");
        var client = await ApiClient.CreateAsync(factory, tenant.Identifier);
        var workspace = await client.CreateWorkspaceAsync("Migration");
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var originalDefinitions = new Dictionary<Guid, string>();
        foreach (var trigger in new[] { "tagAdded", "manual,tagAdded", "tagAdded,itemUpdated", "itemAdded,tagAdded,manual", "manual", "not_tagAdded" })
        {
            var workflow = new WorkflowDefinition { Id = Ids.New(), WorkspaceId = workspace, Name = trigger, Trigger = trigger, Enabled = true, CurrentVersion = 1 };
            db.Workflows.Add(workflow);
            var definition = DefinitionJson.Serialize(new WorkflowSpec(null, null, [new WorkflowStep("delay", Hours: 24)], Triggers: trigger.Split(',').Select(type => new WorkflowTrigger(type)).ToArray(), Scope: "workspace"));
            originalDefinitions[workflow.Id] = definition;
            db.Versions.Add(new WorkflowVersion { Id = Ids.New(), WorkflowId = workflow.Id, Number = 1, Definition = definition });
            db.Runs.Add(new WorkflowRun
            {
                Id = Ids.New(),
                WorkflowId = workflow.Id,
                WorkflowVersion = 1,
                WorkspaceId = workspace,
                Status = RunStatus.Completed,
                Data = """{"legacy":true}""",
                Outputs = """{"legacy":{"result":"preserved"}}""",
                StartedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync(Ct);
        Migration migration = PaperDotNetApiFactory.Provider == "sqlite"
            ? new PaperDotNet.Migrations.Sqlite.Generated.Workflows.ParameterizedItemTriggers()
            : new PaperDotNet.Migrations.PostgreSql.Generated.Workflows.ParameterizedItemTriggers();
        foreach (var operation in migration.UpOperations.OfType<SqlOperation>()) await db.Database.ExecuteSqlRawAsync(operation.Sql, Ct);
        db.ChangeTracker.Clear();
        var definitions = await db.Workflows.AsNoTracking().ToListAsync(Ct);
        Assert.All(definitions.Where(w => w.Trigger.Split(',').Contains("tagAdded")), workflow => Assert.False(workflow.Enabled));
        Assert.All(definitions.Where(w => !w.Trigger.Split(',').Contains("tagAdded")), workflow => Assert.True(workflow.Enabled));
        Assert.All(await db.Versions.AsNoTracking().ToListAsync(Ct), version => Assert.Equal(originalDefinitions[version.WorkflowId], version.Definition));
        foreach (var run in await db.Runs.AsNoTracking().ToListAsync(Ct))
        {
            Assert.Equal(RunStatus.Completed, run.Status);
            Assert.Equal("""{"legacy":true}""", run.Data);
            var response = await client.GetAsync($"/v1.0/workspaces/{workspace}/workflows/runs/{run.Id}", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("preserved", (await response.ReadJsonAsync()).GetProperty("outputs").GetProperty("legacy").GetProperty("result").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1.0/workspaces/{workspace}/workflows/{run.WorkflowId}", Ct)).StatusCode);
        }
    }
}
