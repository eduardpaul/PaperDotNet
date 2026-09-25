using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.IntegrationTests;

public sealed class EventsAndJobsTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] HookedFields =
    [
        new { name = "amount", type = "number" },
        new { name = "status", type = "choice", choices = new[] { "open", "paid" }, defaultValue = "open" },
        new { name = "code", type = "text" },
    ];

    private async Task<(HttpClient Client, Guid Workspace, Guid List, string Tenant)> SetupAsync(string tenant, string listName = "Hooked")
    {
        await factory.CreateTenantAsync(tenant);
        var client = await ApiClient.CreateAsync(factory, tenant);
        var workspace = await client.CreateWorkspaceAsync("Events");
        var contentType = await client.CreateContentTypeAsync("Hooked thing", HookedFields);
        var list = await client.CreateListAsync(workspace, listName, contentType);
        return (client, workspace, list, tenant);
    }

    [Fact]
    public async Task Mutators_can_modify_and_cancel_writes()
    {
        var (client, ws, list, _) = await SetupAsync("mutators");

        var modified = await client.CreateItemAsync(ws, list, new { fields = new { title = "A", code = "abc" } });
        var cancelled = await client.PostItemAsync(ws, list, new { fields = new { title = "forbidden" } });
        var invalid = await client.PostItemAsync(ws, list, new { fields = new { title = "invalid-by-mutator" } });

        Assert.Equal("ABC", modified.GetProperty("fields").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
        var problem = await cancelled.ReadJsonAsync();
        Assert.Equal("cancelledByMutator", problem.GetProperty("code").GetString());
        Assert.Contains("forbidden", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Updating_and_deleting_mutators_can_cancel()
    {
        var (client, ws, list, _) = await SetupAsync("mutators-update");
        var paid = (await client.CreateItemAsync(ws, list, new { fields = new { title = "Invoice", status = "paid" } })).GetProperty("id").GetGuid();
        var keep = (await client.CreateItemAsync(ws, list, new { fields = new { title = "keep me" } })).GetProperty("id").GetGuid();
        var itemUrl = (Guid id) => $"/v1.0/workspaces/{ws}/lists/{list}/items/{id}";

        var update = await client.SendWithEtagAsync(HttpMethod.Patch, itemUrl(paid), (await client.GetAsync(itemUrl(paid), Ct)).Headers.ETag!.Tag, new { fields = new { amount = 5 } });
        var delete = await client.SendWithEtagAsync(HttpMethod.Delete, itemUrl(keep), (await client.GetAsync(itemUrl(keep), Ct)).Headers.ETag!.Tag);

        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(itemUrl(keep), Ct)).StatusCode);
    }

    [Fact]
    public async Task Item_events_are_delivered_in_the_background_inside_their_tenant()
    {
        var (client, ws, list, tenant) = await SetupAsync("events", listName: "Plain");
        var item = await client.CreateItemAsync(ws, list, new { fields = new { title = "Event me", amount = 1 } });
        var id = item.GetProperty("id").GetGuid();
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items/{id}";
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        await client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields = new { amount = 2 } });

        await Eventually.WaitForAsync(() => TestSubscriber.Received.Count(r => r.Event.ItemId == id) >= 2);

        var added = TestSubscriber.Received.Single(r => r.Event.ItemId == id && r.Event is ItemAdded);
        var updated = TestSubscriber.Received.Single(r => r.Event.ItemId == id && r.Event is ItemUpdated);
        Assert.Equal(added.Event.TenantId, added.ResolvedTenant);
        Assert.Equal(tenant, added.Event.TenantIdentifier);
        Assert.Equal(["amount"], updated.Event.ChangedFields);
    }

    [Fact]
    public async Task Bulk_update_runs_as_a_long_running_operation()
    {
        var (client, ws, list, _) = await SetupAsync("bulk", listName: "Plain");
        for (var i = 1; i <= 5; i++)
        {
            await client.CreateItemAsync(ws, list, new { fields = new { title = $"Item {i}", amount = i } });
        }

        var invalid = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/bulkUpdate", new { filter = "fields/nope eq 1", fields = new { status = "paid" } }, Ct);
        var accepted = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/bulkUpdate", new { filter = "fields/amount ge 3", fields = new { status = "paid" } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var location = accepted.Headers.Location!.ToString();
        var operation = await Eventually.WaitForAsync(async () =>
        {
            var body = await (await client.GetAsync(location, Ct)).ReadJsonAsync();
            return body.GetProperty("status").GetString() is "succeeded" or "failed" ? (JsonElement?)body : null;
        });

        Assert.Equal("succeeded", operation.GetProperty("status").GetString());
        Assert.Equal(100, operation.GetProperty("percentComplete").GetInt32());
        Assert.Equal(3, operation.GetProperty("result").GetProperty("updated").GetInt32());
        Assert.Equal(["Item 3", "Item 4", "Item 5"], await client.QueryTitlesAsync(ws, list, "$filter=fields/status eq 'paid'"));

        await factory.CreateTenantAsync("bulk-other");
        var other = await ApiClient.CreateAsync(factory, "bulk-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(location, Ct)).StatusCode);
    }

    [Fact]
    public async Task Recurring_jobs_run_for_every_active_tenant()
    {
        var tenant = await factory.CreateTenantAsync("recurring");

        await Eventually.WaitForAsync(() => TestRecurringJob.Runs.TryGetValue(tenant.Id, out var runs) && runs >= 1, TimeSpan.FromSeconds(30));
    }
}
