using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

public sealed class ApprovalFormTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Workspace_approvals_collect_validated_information_at_multiple_steps()
    {
        await factory.CreateTenantAsync("approval-forms");
        var client = await ApiClient.CreateAsync(factory, "approval-forms");
        var workspace = await client.CreateWorkspaceAsync("Forms");
        var tasks = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists", new { name = "Tasks", templateKey = "tasks" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var root = $"/v1.0/workspaces/{workspace}/workflows";
        var response = await client.PostAsJsonAsync(root, new
        {
            name = "Collect information",
            scope = "workspace",
            trigger = new { type = "manual" },
            steps = new object[]
            {
                new { type = "approval", name = "Review", assignees = new[] { "admin" }, inputSchema = new
                {
                    type = "object", properties = new
                    {
                        amount = new { type = "integer", minimum = 1, maximum = 100 },
                        route = new { type = "string", @enum = new[] { "standard", "express" } },
                        urgent = new { type = "boolean", @default = false },
                    }, required = new[] { "amount", "route" },
                } },
                new { type = "approval", name = "Complete", assignees = new[] { "admin" }, inputSchema = new
                {
                    type = "object", properties = new { note = new { type = "string", minLength = 2 } }, required = new[] { "note" },
                } },
                new { type = "action", action = "task.create", inputs = new { list = "Tasks", title = "{step:Review.input.route}:{step:Complete.input.note}" } },
            },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var workflow = (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
        var started = await client.PostAsJsonAsync($"{root}/{workflow}/runs", new { inputs = new { launch = "original" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var runId = (await started.ReadJsonAsync())[0].GetProperty("id").GetGuid();
        async Task<JsonElement?> PendingAsync(string step)
        {
            var list = await (await client.GetAsync("/v1.0/me/approvals", Ct)).ReadJsonAsync();
            var rows = list.GetProperty("value").EnumerateArray().Where(a => a.GetProperty("stepName").GetString() == step).ToList();
            return rows.Count == 1 ? rows[0] : null;
        }
        var review = await Eventually.WaitForAsync(() => PendingAsync("Review"), TimeSpan.FromSeconds(30));
        Assert.False(review.TryGetProperty("itemId", out _));
        Assert.True(review.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("amount", out _));
        var currentDefinition = await client.GetAsync($"{root}/{workflow}", Ct);
        var replaced = await client.SendWithEtagAsync(HttpMethod.Put, $"{root}/{workflow}", currentDefinition.Headers.ETag!.Tag,
            new { name = "Collect information", scope = "workspace", trigger = new { type = "manual" }, steps = new[] { new { type = "delay", hours = 24 } } });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode); // the waiting run keeps its original schemas and next step
        var decision = $"/v1.0/me/approvals/{review.GetProperty("id").GetGuid()}/decision";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(decision, new { outcome = "approved" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(decision, new { outcome = "rejected", inputs = new { amount = 0, route = "unknown" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(decision, new { outcome = "approved", inputs = new { amount = "10", route = "standard" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(decision, new { outcome = "approved", inputs = new { amount = 10, route = "standard", extra = true } }, Ct)).StatusCode);
        Assert.NotNull(await PendingAsync("Review")); // invalid submissions did not decide or resume it
        var accepted = await client.PostAsJsonAsync(decision, new { outcome = "approved", inputs = new { amount = 10, route = "express" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.False((await accepted.ReadJsonAsync()).GetProperty("inputs").GetProperty("urgent").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(decision, new { outcome = "approved", inputs = new { amount = 20, route = "standard" } }, Ct)).StatusCode);
        var complete = await Eventually.WaitForAsync(() => PendingAsync("Complete"), TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/v1.0/me/approvals/{complete.GetProperty("id").GetGuid()}/decision", new { outcome = "rejected", inputs = new { note = "Finished" } }, Ct)).StatusCode);
        var run = await Eventually.WaitForAsync(async () =>
        {
            var value = await (await client.GetAsync($"{root}/runs/{runId}", Ct)).ReadJsonAsync();
            return value.GetProperty("status").GetString() == "completed" ? value : (JsonElement?)null;
        }, TimeSpan.FromSeconds(30));
        var outputs = run.GetProperty("outputs");
        Assert.Equal(10, outputs.GetProperty("Review").GetProperty("input").GetProperty("amount").GetInt32());
        Assert.Equal("Finished", outputs.GetProperty("Complete").GetProperty("input").GetProperty("note").GetString());
        Assert.Equal("original", run.GetProperty("executionContext").GetProperty("input").GetProperty("launch").GetString());
        Assert.Equal("rejected", outputs.GetProperty("Complete").GetProperty("outcome").GetString());
        Assert.Contains("express:Finished", await client.QueryTitlesAsync(workspace, tasks, ""));
        var history = await (await client.GetAsync("/v1.0/me/approvals?status=approved", Ct)).ReadJsonAsync();
        Assert.Equal("express", history.GetProperty("value")[0].GetProperty("inputs").GetProperty("route").GetString());
    }
}
