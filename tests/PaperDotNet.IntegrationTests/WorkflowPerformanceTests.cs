using System.Data.Common;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Measures event fan-out and guards against one definition query per workflow.</summary>
public sealed class WorkflowPerformanceTests(PaperDotNetApiFactory factory)
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Event_definition_queries_stay_constant_as_workflow_fanout_grows()
    {
        var tenant = await factory.CreateTenantAsync("workflow-performance");
        var client = await ApiClient.CreateAsync(factory, tenant.Identifier);
        var workspace = await client.CreateWorkspaceAsync("Performance");
        var list = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists", new { name = "Tasks", templateKey = "tasks" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var item = await client.CreateItemAsync(workspace, list, new { fields = new { title = "Tagged" } });
        // Provision product defaults before measuring custom-workflow fan-out, then isolate those candidates.
        await client.GetAsync($"/v1.0/workspaces/{workspace}/lists/{list}/workflows/builtIns", Ct);
        var scopes = factory.Services.GetRequiredService<ITenantScopeFactory>();
        await using var scope = scopes.CreateScope(tenant.Id, tenant.Identifier);
        var services = scope.ServiceProvider;
        await services.GetRequiredService<WorkflowsDbContext>().Workflows
            .Where(w => w.WorkspaceId == workspace && w.BuiltInKey != null).ExecuteUpdateAsync(u => u.SetProperty(w => w.Enabled, false), Ct);
        var counter = new VersionReads();
        var options = new DbContextOptionsBuilder<WorkflowsDbContext>(services.GetRequiredService<DbContextOptions<WorkflowsDbContext>>()).AddInterceptors(counter).Options;
        await using var db = new WorkflowsDbContext(options, services.GetRequiredService<ITenantContext>());
        var countedItems = DispatchProxy.Create<IListItemStore, ItemReads>();
        var itemReads = (ItemReads)(object)countedItems;
        itemReads.Target = services.GetRequiredService<IListItemStore>().AsSystem();
        var runService = ActivatorUtilities.CreateInstance<RunService>(services, db);
        var starter = ActivatorUtilities.CreateInstance<WorkflowStarter>(services, db, runService, countedItems);
        var builtIns = ActivatorUtilities.CreateInstance<BuiltInWorkflows>(services, db);
        var handler = ActivatorUtilities.CreateInstance<WorkflowTriggerHandler>(services, db, starter, builtIns, countedItems);
        var measurements = new List<object>();
        var previous = 0;
        foreach (var count in new[] { 1, 10, 50 })
        {
            for (var i = previous; i < count; i++)
            {
                var workflow = new WorkflowDefinition { Id = Ids.New(), WorkspaceId = workspace, Name = $"Tag {i}", Trigger = "itemUpdated", Enabled = true, CurrentVersion = 1 };
                db.Workflows.Add(workflow);
                db.Versions.Add(new WorkflowVersion
                {
                    Id = Ids.New(),
                    WorkflowId = workflow.Id,
                    Number = 1,
                    Definition = DefinitionJson.Serialize(new WorkflowSpec(new WorkflowTrigger("itemUpdated", Parameters: new WorkflowTriggerParameters(JsonNode.Parse("""{"target":"field","field":"title","operator":"eq","value":"Tagged"}""")!.AsObject())), null, [new WorkflowStep("delay", Hours: 24)], Scope: "workspace"))
                });
            }
            await db.SaveChangesAsync(Ct);
            previous = count;
            counter.Count = 0;
            itemReads.Count = 0;
            var source = new ItemUpdated
            {
                EventId = Ids.New(),
                TenantId = tenant.Id,
                TenantIdentifier = tenant.Identifier,
                WorkspaceId = workspace,
                ListId = list,
                ItemId = item.GetProperty("id").GetGuid(),
                ContentTypeId = item.GetProperty("contentTypeId").GetGuid(),
                Before = new ItemSnapshot(item.GetProperty("id").GetGuid(), workspace, list, "Tasks", item.GetProperty("contentTypeId").GetGuid(), "Task", "task", null, false,
                    new JsonObject { ["title"] = "Before" }, new Dictionary<string, ItemSnapshotField> { ["title"] = new("text", false) }),
                After = new ItemSnapshot(item.GetProperty("id").GetGuid(), workspace, list, "Tasks", item.GetProperty("contentTypeId").GetGuid(), "Task", "task", null, false,
                    new JsonObject { ["title"] = "Tagged" }, new Dictionary<string, ItemSnapshotField> { ["title"] = new("text", false) }),
                ChangedFields = ["tags"]
            };
            var watch = Stopwatch.StartNew();
            await handler.HandleAsync(source, Ct);
            watch.Stop();
            var reads = counter.Count;
            Assert.Equal(0, itemReads.Count); // snapshot conditions need no item reads or condition queries
            Assert.Equal(1, reads); // one joined candidate/version query for the item event
            Assert.Equal(count, await db.Runs.CountAsync(r => r.EventId == source.EventId, Ct));
            counter.Count = 0;
            await handler.HandleAsync(source, Ct);
            Assert.Equal(1, counter.Count); // redelivery stays bounded and creates nothing
            Assert.Equal(count, await db.Runs.CountAsync(r => r.EventId == source.EventId, Ct));
            measurements.Add(new { workflows = count, versionQueries = reads, handlerMilliseconds = watch.Elapsed.TotalMilliseconds, eventPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(source, DefinitionJson.Options).Length });
        }
        var requestMeasurements = new List<object>();
        async Task MeasureAsync(string name, Func<Task<HttpResponseMessage>> send, int sampleCount = 5)
        {
            var samples = new List<double>();
            for (var repeat = 0; repeat < sampleCount; repeat++)
            {
                var watch = Stopwatch.StartNew();
                var response = await send();
                await response.Content.ReadAsByteArrayAsync(Ct);
                watch.Stop();
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            requestMeasurements.Add(new { name, samples = samples.Count, medianMilliseconds = samples[samples.Count / 2], maxMilliseconds = samples[^1] });
        }
        var root = $"/v1.0/workspaces/{workspace}/workflows";
        await MeasureAsync("workflow-list-50", () => client.GetAsync(root, Ct));
        await MeasureAsync("run-list-61", () => client.GetAsync($"{root}/runs?$top=100", Ct));
        var manualResponse = await client.PostAsJsonAsync(root, new
        {
            name = "Manual form",
            scope = "workspace",
            trigger = new { type = "manual" },
            inputSchema = new { type = "object", properties = new { amount = new { type = "integer", minimum = 1 }, urgent = new { type = "boolean", @default = false } }, required = new[] { "amount" } },
            steps = new[] { new { type = "delay", hours = 24 } }
        }, Ct);
        Assert.True(manualResponse.IsSuccessStatusCode, await manualResponse.Content.ReadAsStringAsync(Ct));
        var manualId = (await manualResponse.ReadJsonAsync()).GetProperty("id").GetGuid();
        await MeasureAsync("manual-launch-with-schema", () => client.PostAsJsonAsync($"{root}/{manualId}/runs", new { inputs = new { amount = 10 } }, Ct));
        var approvalResponse = await client.PostAsJsonAsync(root, new
        {
            name = "Approval form",
            scope = "workspace",
            trigger = new { type = "manual" },
            steps = new[] { new { type = "approval", name = "Form", assignees = new[] { "admin" }, inputSchema = new { type = "object", properties = new { amount = new { type = "integer", minimum = 1 } }, required = new[] { "amount" } } } }
        }, Ct);
        Assert.True(approvalResponse.IsSuccessStatusCode, await approvalResponse.Content.ReadAsStringAsync(Ct));
        var approvalWorkflowId = (await approvalResponse.ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.True((await client.PostAsJsonAsync($"{root}/{approvalWorkflowId}/runs", new { }, Ct)).IsSuccessStatusCode);
        var approval = await Eventually.WaitForAsync(async () =>
        {
            var rows = (await (await client.GetAsync("/v1.0/me/approvals", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
            return rows.Count == 1 ? rows[0] : (JsonElement?)null;
        }, TimeSpan.FromSeconds(30));
        await MeasureAsync("approval-list-with-schema", () => client.GetAsync("/v1.0/me/approvals", Ct));
        await MeasureAsync("approval-decision-with-schema", () => client.PostAsJsonAsync($"/v1.0/me/approvals/{approval.GetProperty("id").GetGuid()}/decision", new { outcome = "approved", inputs = new { amount = 10 } }, Ct), sampleCount: 1);
        var payloadMeasurements = new List<object>();
        foreach (var fieldCount in new[] { 1, 10, 50 })
        {
            var fields = new JsonObject();
            var fieldTypes = new Dictionary<string, ItemSnapshotField>();
            for (var i = 0; i < fieldCount; i++)
            {
                fields[$"field{i}"] = new string('a', 128);
                fieldTypes[$"field{i}"] = new("text", false);
            }
            var snapshot = new ItemSnapshot(item.GetProperty("id").GetGuid(), workspace, list, "Tasks", item.GetProperty("contentTypeId").GetGuid(), "Task", "task", null, false, fields, fieldTypes);
            var source = new ItemUpdated
            {
                TenantId = tenant.Id,
                TenantIdentifier = tenant.Identifier,
                WorkspaceId = workspace,
                ListId = list,
                ItemId = snapshot.ItemId,
                ContentTypeId = snapshot.ContentTypeId,
                Before = snapshot,
                After = snapshot,
                ChangedFields = ["field0"],
            };
            payloadMeasurements.Add(new { fields = fieldCount, charactersPerField = 128, eventPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(source, DefinitionJson.Options).Length });
        }
        var json = JsonSerializer.Serialize(new { provider = PaperDotNetApiFactory.Provider, workload = "Snapshot-based parameterized itemUpdated delivery; transactional run creation; background execution excluded from handler timer; HTTP samples use in-process test host (no network)", measurements, requestMeasurements, payloadMeasurements }, ReportJson);
        TestContext.Current.TestOutputHelper!.WriteLine(json);
        if (Environment.GetEnvironmentVariable("PAPERDOTNET_WORKFLOW_PERF_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, json + "\n", Ct);
        }
    }

    public class ItemReads : DispatchProxy
    {
        public IListItemStore Target { get; set; } = null!;
        public int Count { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IListItemStore.AsSystem)) return this;
            if (targetMethod.Name is nameof(IListItemStore.GetAsync) or nameof(IListItemStore.QueryAsync)) Count++;
            return targetMethod.Invoke(Target, args);
        }
    }

    private sealed class VersionReads : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var query = command.CommandText.TrimStart();
            // PostgreSQL prepends its tenant SET to queries outside a transaction.
            if (query.StartsWith("SET ", StringComparison.OrdinalIgnoreCase) && query.IndexOf('\n') is var separator && separator >= 0)
            {
                query = query[(separator + 1)..].TrimStart();
            }
            if (query.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && query.Contains("versions", StringComparison.OrdinalIgnoreCase)) Count++;
            return ValueTask.FromResult(result);
        }
    }
}
