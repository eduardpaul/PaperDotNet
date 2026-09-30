using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Querying;

namespace PaperDotNet.IntegrationTests;

/// <summary>Queries across lists run as one query per group of same-shaped lists (ADR-0035 step 5).</summary>
public sealed class CrossListQueryTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task My_tasks_come_from_every_readable_task_list_in_one_pass()
    {
        var tenant = await factory.CreateTenantAsync("cross-tasks");
        var admin = await ApiClient.CreateAsync(factory, "cross-tasks");
        var alice = (await (await admin.PostAsJsonAsync("/v1.0/users", new { userName = "alice", password = "alice-password-1" }, Ct)).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        var lists = new List<(Guid Workspace, Guid List)>();
        foreach (var name in new[] { "Ops", "Dev" })
        {
            var ws = await admin.CreateWorkspaceAsync(name);
            await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = alice, role = "member" }, Ct);
            foreach (var list in new[] { "Sprint", "Backlog" })
            {
                var created = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = list, templateKey = "tasks" }, Ct);
                lists.Add((ws, (await created.ReadJsonAsync()).GetProperty("id").GetGuid()));
            }
        }

        // One task per list for alice, due on different days; one done, one for someone else.
        for (var n = 0; n < lists.Count; n++)
        {
            var (ws, list) = lists[n];
            await admin.CreateItemAsync(ws, list, new { fields = new { title = $"Task {n}", dueDate = $"2026-10-0{n + 1}", assignedTo = new[] { alice } } });
        }

        await admin.CreateItemAsync(lists[0].Workspace, lists[0].List, new { fields = new { title = "Done", status = "completed", assignedTo = new[] { alice } } });
        await admin.CreateItemAsync(lists[1].Workspace, lists[1].List, new { fields = new { title = "Not mine" } });

        // A task in a folder alice cannot read stays out.
        var (hiddenWs, hiddenList) = lists[2];
        var folder = (await admin.CreateItemAsync(hiddenWs, hiddenList, new { isFolder = true, fields = new { title = "Private" } })).GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync($"/v1.0/workspaces/{hiddenWs}/lists/{hiddenList}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        await admin.CreateItemAsync(hiddenWs, hiddenList, new { parentId = folder, fields = new { title = "Secret", assignedTo = new[] { alice } } });

        var client = await ApiClient.CreateAsync(factory, "cross-tasks", "alice", "alice-password-1");
        var mine = await (await client.GetAsync("/v1.0/me/tasks", Ct)).ReadJsonAsync();
        Assert.Equal(["Task 0", "Task 1", "Task 2", "Task 3"], mine.GetProperty("value").EnumerateArray().Select(t => t.GetProperty("title").GetString()));

        // Lists made from one template share the query shape, so the four lists are one query.
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var schemas = await scope.ServiceProvider.GetRequiredService<ListSchemaLoader>().LoadManyAsync([.. lists.Select(l => l.List)], system: true, Ct);
        Assert.Single(schemas.GroupBy(ItemQueryRunner.QueryShape));
    }
}
