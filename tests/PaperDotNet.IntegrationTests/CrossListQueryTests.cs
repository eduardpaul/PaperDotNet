using System.Net;

namespace PaperDotNet.IntegrationTests;

/// <summary>Queries across lists (TSK-04): the caller's tasks from every task list they can read.</summary>
public sealed class CrossListQueryTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task My_tasks_come_from_every_readable_task_list()
    {
        var admin = await _host.SignInAsync();
        using (var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "alice", password = "alice-password-1" }, Ct))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var client = await _host.SignInAsync("alice", "alice-password-1");
        var alice = (await (await client.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id();
        var lists = new List<(string Workspace, string List)>();
        foreach (var name in new[] { "Ops", "Dev" })
        {
            var ws = await Api.CreateWorkspaceAsync(admin, name);
            await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = alice, role = "member" }, Ct);
            foreach (var list in new[] { "Sprint", "Backlog" })
            {
                using var created = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = list, templateKey = "tasks" }, Ct);
                lists.Add((ws, (await created.JsonAsync(HttpStatusCode.Created)).Id()));
            }
        }

        // One task per list for alice, due on different days; one done, one for someone else.
        for (var n = 0; n < lists.Count; n++)
        {
            await Api.CreateItemAsync(admin, lists[n].Workspace, lists[n].List, new { title = $"Task {n}", dueDate = $"2026-10-0{n + 1}", assignedTo = new[] { alice } });
        }

        await Api.CreateItemAsync(admin, lists[0].Workspace, lists[0].List, new { title = "Done", status = "completed", assignedTo = new[] { alice } });
        await Api.CreateItemAsync(admin, lists[1].Workspace, lists[1].List, new { title = "Not mine" });

        // A task in a folder alice cannot read stays out.
        var (hiddenWs, hiddenList) = lists[2];
        using var folderResponse = await admin.PostAsJsonAsync(Api.Items(hiddenWs, hiddenList), new { isFolder = true, fields = new { title = "Private" } }, Ct);
        var folder = (await folderResponse.JsonAsync(HttpStatusCode.Created)).Id();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/v1.0/workspaces/{hiddenWs}/lists/{hiddenList}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        using (var secret = await admin.PostAsJsonAsync(Api.Items(hiddenWs, hiddenList), new { parentId = folder, fields = new { title = "Secret", assignedTo = new[] { alice } } }, Ct))
        {
            await secret.JsonAsync(HttpStatusCode.Created);
        }

        var mine = await (await client.GetAsync("/v1.0/me/tasks", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(["Task 0", "Task 1", "Task 2", "Task 3"], mine.GetProperty("value").EnumerateArray().Select(t => t.GetProperty("title").GetString()));
    }
}
