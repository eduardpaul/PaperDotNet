using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Bulk updates as long-running operations (LST-05, EVT-06).</summary>
public sealed class BulkUpdateTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<JsonElement> FinishedAsync(HttpClient client, string location)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var operation = await (await client.GetAsync(location, Ct)).JsonAsync(HttpStatusCode.OK);
            if (operation.GetProperty("status").GetString() is "succeeded" or "failed")
            {
                return operation;
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail("The operation did not finish.");
        return default;
    }

    [Fact]
    public async Task Bulk_update_runs_as_a_long_running_operation()
    {
        var client = await _host.SignInAsync();
        var ws = await Api.CreateWorkspaceAsync(client, "Finance");
        var list = (await Api.CreateListAsync(client, ws, "Invoices", new object[]
        {
            new { name = "amount", type = "number", required = true },
            new { name = "status", type = "choice", choices = new[] { "open", "paid" } },
        })).Id();
        for (var i = 1; i <= 5; i++)
        {
            await Api.CreateItemAsync(client, ws, list, new { title = $"Item {i}", amount = i });
        }

        var bulk = $"{Api.Items(ws, list)}/bulkUpdate";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(bulk, new { filter = "fields/nope eq 1", fields = new { status = "paid" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(bulk, new { filter = "fields/amount ge 3", fields = new { } }, Ct)).StatusCode);

        using var accepted = await client.PostAsJsonAsync(bulk, new { filter = "fields/amount ge 3", fields = new { status = "paid" } }, Ct);
        Assert.Equal("notStarted", (await accepted.JsonAsync(HttpStatusCode.Accepted)).GetProperty("status").GetString());
        var location = accepted.Headers.Location!.ToString();
        var operation = await FinishedAsync(client, location);
        Assert.Equal("succeeded", operation.GetProperty("status").GetString());
        Assert.Equal(3, operation.GetProperty("result").GetProperty("updated").GetInt32());
        var paid = (await (await client.GetAsync($"{Api.Items(ws, list)}?$filter=fields/status eq 'paid'", Ct)).JsonAsync(HttpStatusCode.OK))
            .GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal(["Item 3", "Item 4", "Item 5"], paid);

        // Values each item rejects are reported per item.
        using var invalid = await client.PostAsJsonAsync(bulk, new { fields = new { amount = (int?)null } }, Ct);
        await invalid.JsonAsync(HttpStatusCode.Accepted);
        var failed = await FinishedAsync(client, invalid.Headers.Location!.ToString());
        var result = failed.GetProperty("result");
        Assert.Equal(5, result.GetProperty("failed").GetInt32());
        Assert.Equal(5, result.GetProperty("failures").GetArrayLength());

        var other = await _host.CreateTenantAsync("other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(location, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(bulk, new { fields = new { status = "paid" } }, Ct)).StatusCode);
    }
}
