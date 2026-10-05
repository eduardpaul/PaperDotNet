using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

public sealed class SelectionWorkflowTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private async Task<(HttpClient Client, Guid Workspace, Guid List, Guid[] Items)> SetupAsync(string suffix)
    {
        await factory.CreateTenantAsync("selection-" + suffix.ToLowerInvariant());
        var client = await ApiClient.CreateAsync(factory, "selection-" + suffix.ToLowerInvariant());
        var ws = await client.CreateWorkspaceAsync("Selection");
        var list = await CreateAsync(client, $"/v1.0/workspaces/{ws}/lists", new { name = "Targets", templateKey = "tasks" });
        var items = new List<Guid>();
        for (var i = 0; i < 3; i++) items.Add(await CreateAsync(client, $"/v1.0/workspaces/{ws}/lists/{list}/items", new { fields = new { title = $"Target {i}" } }));
        return (client, ws, list, items.ToArray());
    }
    private static async Task<Guid> CreateAsync(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }
    internal static async Task<JsonElement> WaitAsync(HttpClient client, Guid ws, Guid run, string status)
    {
        var result = await Eventually.WaitForAsync(async () =>
    {
        var response = await client.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs/{run}", Ct);
        var body = await response.ReadJsonAsync();
        return body.GetProperty("status").GetString() == status || body.GetProperty("status").GetString() is "failed" or "cancelled" ? body : (JsonElement?)null;
    }, TimeSpan.FromSeconds(60));
        Assert.True(result.GetProperty("status").GetString() == status, result.ToString());
        return result;
    }

    [Theory]
    [InlineData("selection", 1)]
    [InlineData("perItem", 3)]
    public async Task Selection_is_ordered_durable_and_exposed_to_scripts_while_per_item_is_compatible(string mode, int count)
    {
        var (client, ws, list, ids) = await SetupAsync(mode);
        var root = $"/v1.0/workspaces/{ws}/workflows";
        var workflow = await CreateAsync(client, root, new
        {
            name = "Inspect",
            scope = "list",
            trigger = new { type = "manual", list = "Targets", selectionMode = mode },
            flow = new { start = "inspect", nodes = new { inspect = new { activity = "script", inputs = new { code = "return { selected: context.items, primary: context.itemId };" } } } }
        });
        var order = new[] { ids[2], ids[0], ids[1], ids[2] };
        var launch = await client.PostAsJsonAsync($"{root}/{workflow}/runs", new { listId = list, itemIds = order, primaryItemId = ids[0] }, Ct);
        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        var runs = (await launch.ReadJsonAsync()).EnumerateArray().ToArray();
        Assert.Equal(count, runs.Length);
        foreach (var run in runs)
        {
            var completed = await WaitAsync(client, ws, run.GetProperty("id").GetGuid(), "completed");
            var context = completed.GetProperty("executionContext");
            var members = context.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("itemId").GetGuid()).ToArray();
            Assert.Equal(mode == "selection" ? order.Distinct().ToArray() : new[] { run.GetProperty("itemId").GetGuid() }, members);
            Assert.Equal(members, completed.GetProperty("outputs").GetProperty("inspect").GetProperty("result").GetProperty("selected").EnumerateArray().Select(i => i.GetProperty("itemId").GetGuid()));
            if (mode == "selection") Assert.Equal(ids[0], context.GetProperty("itemId").GetGuid());
        }
        var tenant = await factory.Services.GetRequiredService<PaperDotNet.Tenancy.Contracts.ITenantDirectory>().FindAsync("selection-" + mode.ToLowerInvariant(), Ct);
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant!.Id, tenant.Identifier);
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>().RunItems.CountAsync(Ct));
        var memberRuns = await client.GetAsync($"{root}/runs?itemId={ids[1]}", Ct);
        Assert.Single((await memberRuns.ReadJsonAsync()).GetProperty("value").EnumerateArray());
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("replace")]
    public async Task Overlap_is_checked_across_all_members_even_when_primary_items_differ(string concurrency)
    {
        var (client, ws, list, ids) = await SetupAsync(concurrency);
        var root = $"/v1.0/workspaces/{ws}/workflows";
        var workflow = await CreateAsync(client, root, new
        {
            name = "Review",
            scope = "list",
            concurrency,
            trigger = new { type = "manual", list = "Targets", selectionMode = "selection" },
            steps = new[] { new { type = "approval", name = "Review", assignees = new[] { "admin" } } }
        });
        var first = await client.PostAsJsonAsync($"{root}/{workflow}/runs", new { listId = list, itemIds = new[] { ids[0], ids[1] } }, Ct);
        var runId = (await first.ReadJsonAsync())[0].GetProperty("id").GetGuid();
        await WaitAsync(client, ws, runId, "waiting");
        var second = await client.PostAsJsonAsync($"{root}/{workflow}/runs", new { listId = list, itemIds = new[] { ids[2], ids[1] } }, Ct);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var next = (await second.ReadJsonAsync()).EnumerateArray().ToArray();
        Assert.Equal(concurrency == "skip" ? 0 : 1, next.Length);
        await WaitAsync(client, ws, runId, concurrency == "skip" ? "waiting" : "cancelled");
    }

    [Fact]
    public async Task Expired_selection_run_is_recovered_in_a_new_scope_with_its_order_and_primary()
    {
        var (client, ws, list, ids) = await SetupAsync("recovery");
        var root = $"/v1.0/workspaces/{ws}/workflows";
        var workflow = await CreateAsync(client, root, new
        {
            name = "Recover selection",
            scope = "list",
            trigger = new { type = "manual", list = "Targets", selectionMode = "selection" },
            flow = new { start = "inspect", nodes = new { inspect = new { activity = "script", inputs = new { code = "return { selected: context.items, primary: context.itemId };" } } } }
        });
        var order = new[] { ids[2], ids[0], ids[1] };
        var launch = await client.PostAsJsonAsync($"{root}/{workflow}/runs", new { listId = list, itemIds = order, primaryItemId = ids[0] }, Ct);
        var first = (await launch.ReadJsonAsync())[0].GetProperty("id").GetGuid();
        await WaitAsync(client, ws, first, "completed");
        var tenant = await factory.Services.GetRequiredService<PaperDotNet.Tenancy.Contracts.ITenantDirectory>().FindAsync("selection-recovery", Ct);
        var scopes = factory.Services.GetRequiredService<ITenantScopeFactory>();
        var recoveredId = Guid.CreateVersion7();
        await using (var scope = scopes.CreateScope(tenant!.Id, tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var original = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == first, Ct);
            var old = DateTimeOffset.UtcNow.AddMinutes(-10);
            db.Runs.Add(new WorkflowRun
            {
                Id = recoveredId,
                WorkflowId = workflow,
                WorkflowVersion = original.WorkflowVersion,
                WorkspaceId = ws,
                ListId = list,
                ItemId = ids[0],
                IsSelection = true,
                Data = original.Data,
                StartedBy = original.StartedBy,
                StartedAt = old,
                LastActivityAt = old,
                LeaseUntil = old,
                Status = RunStatus.Running
            });
            for (var i = 0; i < order.Length; i++) db.RunItems.Add(new WorkflowRunItem { RunId = recoveredId, WorkspaceId = ws, ListId = list, ItemId = order[i], Position = i });
            await db.SaveChangesAsync(Ct);
        }
        await using (var scope = scopes.CreateScope(tenant!.Id, tenant.Identifier))
            await scope.ServiceProvider.GetRequiredService<WorkflowTimerJob>().RunAsync(Ct);
        var completed = await WaitAsync(client, ws, recoveredId, "completed");
        var result = completed.GetProperty("outputs").GetProperty("inspect").GetProperty("result");
        Assert.Equal(order, result.GetProperty("selected").EnumerateArray().Select(i => i.GetProperty("itemId").GetGuid()));
        Assert.Equal(ids[0], result.GetProperty("primary").GetGuid());
    }

    [Fact]
    public async Task Invalid_selection_or_input_starts_nothing_and_deleting_any_member_cancels_review()
    {
        var (client, ws, list, ids) = await SetupAsync("validation");
        var root = $"/v1.0/workspaces/{ws}/workflows";
        var workflow = await CreateAsync(client, root, new
        {
            name = "Review",
            scope = "list",
            trigger = new { type = "manual", list = "Targets", selectionMode = "selection" },
            inputSchema = new { type = "object", properties = new { label = new { type = "string" } }, required = new[] { "label" } },
            steps = new[] { new { type = "approval", name = "Review", assignees = new[] { "admin" } } }
        });
        foreach (var body in new object[] {
            new { listId = list, itemIds = ids, primaryItemId = Guid.NewGuid(), inputs = new { label = "valid" } },
            new { listId = list, itemIds = Array.Empty<Guid>(), inputs = new { label = "valid" } },
            new { listId = list, itemIds = new[] { ids[0], Guid.NewGuid() }, inputs = new { label = "valid" } },
            new { listId = list, itemIds = ids },
        }) Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/{workflow}/runs", body, Ct)).StatusCode);
        var none = await client.GetAsync($"{root}/runs?workflowId={workflow}", Ct);
        Assert.Empty((await none.ReadJsonAsync()).GetProperty("value").EnumerateArray());
        var launch = await client.PostAsJsonAsync($"{root}/{workflow}/runs", new { listId = list, itemIds = ids, inputs = new { label = "valid" } }, Ct);
        var runId = (await launch.ReadJsonAsync())[0].GetProperty("id").GetGuid();
        await WaitAsync(client, ws, runId, "waiting");
        var itemPath = $"/v1.0/workspaces/{ws}/lists/{list}/items/{ids[2]}";
        var current = await client.GetAsync(itemPath, Ct);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, itemPath);
        delete.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete, Ct)).StatusCode);
        await WaitAsync(client, ws, runId, "cancelled");
    }
}
