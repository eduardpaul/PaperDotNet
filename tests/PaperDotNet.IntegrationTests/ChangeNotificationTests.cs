using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Notifications.Features;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Change notifications for API clients (API-06).</summary>
public sealed class ChangeNotificationTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TenantSummary Tenant, HttpClient Admin, Guid Workspace, Guid List)
    {
        public string Resource => $"workspaces/{Workspace}/lists/{List}/items";
    }

    private async Task<Setup> SetupAsync(string tenantName)
    {
        var tenant = await factory.CreateTenantAsync(tenantName);
        var admin = await ApiClient.CreateAsync(factory, tenantName);
        var ws = await admin.CreateWorkspaceAsync("Hooks");
        var contentType = await admin.CreateContentTypeAsync("Entry", [new { name = "note", type = "text" }]);
        return new Setup(tenant, admin, ws, await admin.CreateListAsync(ws, "Entries", contentType));
    }

    private async Task DispatchAsync(TenantSummary tenant)
    {
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        await scope.ServiceProvider.GetRequiredService<ChangeDispatcher>().RunAsync(Ct);
    }

    /// <summary>Waits for change notifications posted to <paramref name="path"/>.</summary>
    private async Task<List<(Dictionary<string, string> Headers, JsonElement Change)>> ReceivedAsync(TenantSummary tenant, string path, int count)
    {
        List<(Dictionary<string, string>, JsonElement)> Changes() => TestWebhookReceiver.Instance.Requests
            .Where(r => r.Url.AbsolutePath == path && r.Headers.GetValueOrDefault("X-PaperDotNet-Event") == "change")
            .Select(r => (r.Headers, JsonDocument.Parse(r.Body).RootElement.GetProperty("value")[0]))
            .ToList();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Changes().Count < count && DateTime.UtcNow < deadline)
        {
            await DispatchAsync(tenant);
            await Task.Delay(200, Ct);
        }

        return Changes();
    }

    [Fact]
    public async Task Subscribers_get_signed_notifications_for_changes_they_can_read()
    {
        var setup = await SetupAsync("changes-basic");
        var created = await setup.Admin.PostAsJsonAsync("/v1.0/changeSubscriptions", new
        {
            resource = setup.Resource,
            changeTypes = new[] { "created", "deleted" },
            notificationUrl = "https://hooks.example.test/changes-basic",
            clientState = "s3cr3t-state",
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var subscription = await created.ReadJsonAsync();
        var secret = subscription.GetProperty("secret").GetString()!;
        var id = subscription.GetProperty("id").GetGuid();
        Assert.Contains(TestWebhookReceiver.Instance.Requests, r => r.Url.AbsolutePath == "/changes-basic" && r.Url.Query.Contains("validationToken=", StringComparison.Ordinal));
        Assert.False((await (await setup.Admin.GetAsync($"/v1.0/changeSubscriptions/{id}", Ct)).ReadJsonAsync()).TryGetProperty("secret", out _));

        var item = (await setup.Admin.CreateItemAsync(setup.Workspace, setup.List, new { fields = new { title = "One" } })).GetProperty("id").GetGuid();
        var etag = (await setup.Admin.GetAsync($"/v1.0/workspaces/{setup.Workspace}/lists/{setup.List}/items/{item}", Ct)).Headers.ETag!.Tag;
        await setup.Admin.SendWithEtagAsync(HttpMethod.Patch, $"/v1.0/workspaces/{setup.Workspace}/lists/{setup.List}/items/{item}", etag, new { fields = new { note = "x" } });

        var (headers, change) = Assert.Single(await ReceivedAsync(setup.Tenant, "/changes-basic", 1));
        Assert.Equal("created", change.GetProperty("changeType").GetString());
        Assert.Equal("s3cr3t-state", change.GetProperty("clientState").GetString());
        Assert.Equal(id, change.GetProperty("subscriptionId").GetGuid());
        Assert.Equal(item, change.GetProperty("resourceData").GetProperty("id").GetGuid());
        Assert.Equal($"{setup.Resource}/{item}", change.GetProperty("resource").GetString());
        var request = TestWebhookReceiver.Instance.Requests.Last(r => r.Url.AbsolutePath == "/changes-basic" && r.Headers.GetValueOrDefault("X-PaperDotNet-Event") == "change");
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{headers["X-PaperDotNet-Timestamp"]}.{request.Body}")));
        Assert.Equal(expected, headers["X-PaperDotNet-Signature"]);

        // Renewal needs the ETag; deleting stops notifications.
        var current = await setup.Admin.GetAsync($"/v1.0/changeSubscriptions/{id}", Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await setup.Admin.PatchAsJsonAsync($"/v1.0/changeSubscriptions/{id}", new { expirationDateTime = DateTimeOffset.UtcNow.AddDays(2) }, Ct)).StatusCode);
        var renewed = await setup.Admin.SendWithEtagAsync(HttpMethod.Patch, $"/v1.0/changeSubscriptions/{id}", current.Headers.ETag!.Tag, new { expirationDateTime = DateTimeOffset.UtcNow.AddDays(2) });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Admin.DeleteAsync($"/v1.0/changeSubscriptions/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Admin.GetAsync($"/v1.0/changeSubscriptions/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Subscriptions_are_validated()
    {
        var setup = await SetupAsync("changes-validation");
        async Task<HttpStatusCode> CreateAsync(object body) => (await setup.Admin.PostAsJsonAsync("/v1.0/changeSubscriptions", body, Ct)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { resource = "users", changeTypes = new[] { "created" }, notificationUrl = "https://hooks.example.test/v" }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { resource = setup.Resource, changeTypes = new[] { "moved" }, notificationUrl = "https://hooks.example.test/v" }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { resource = setup.Resource, changeTypes = new[] { "created" }, notificationUrl = "http://hooks.example.test/v" }));
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new
        {
            resource = setup.Resource,
            changeTypes = new[] { "created" },
            notificationUrl = "https://hooks.example.test/v",
            expirationDateTime = DateTimeOffset.UtcNow.AddDays(40),
        }));

        // The receiver must echo the validation token.
        Assert.Equal(HttpStatusCode.BadRequest, await CreateAsync(new { resource = setup.Resource, changeTypes = new[] { "created" }, notificationUrl = "https://fail.example.test/v" }));
        Assert.Equal(HttpStatusCode.NotFound, await CreateAsync(new
        {
            resource = $"workspaces/{setup.Workspace}/lists/{Guid.NewGuid()}/items",
            changeTypes = new[] { "created" },
            notificationUrl = "https://hooks.example.test/v",
        }));
    }

    [Fact]
    public async Task Owners_only_hear_about_items_they_can_read_and_other_tenants_see_nothing()
    {
        var setup = await SetupAsync("changes-access");
        var alice = await setup.Admin.PostAsJsonAsync("/v1.0/users", new { userName = "alice", password = "alice-password-1" }, Ct);
        await setup.Admin.PostAsJsonAsync($"/v1.0/workspaces/{setup.Workspace}/members", new { userId = (await alice.ReadJsonAsync()).GetProperty("id").GetGuid(), role = "member" }, Ct);
        var folder = (await setup.Admin.CreateItemAsync(setup.Workspace, setup.List, new { isFolder = true, fields = new { title = "Private" } })).GetProperty("id").GetGuid();
        await setup.Admin.PostAsJsonAsync($"/v1.0/workspaces/{setup.Workspace}/lists/{setup.List}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);

        var client = await ApiClient.CreateAsync(factory, setup.Tenant.Identifier, "alice", "alice-password-1");
        var created = await client.PostAsJsonAsync("/v1.0/changeSubscriptions",
            new { resource = setup.Resource, changeTypes = new[] { "created" }, notificationUrl = "https://hooks.example.test/changes-access" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();

        await setup.Admin.CreateItemAsync(setup.Workspace, setup.List, new { parentId = folder, fields = new { title = "Hidden" } });
        var visible = (await setup.Admin.CreateItemAsync(setup.Workspace, setup.List, new { fields = new { title = "Visible" } })).GetProperty("id").GetGuid();
        var changes = await ReceivedAsync(setup.Tenant, "/changes-access", 1);
        await Task.Delay(1000, Ct);
        await DispatchAsync(setup.Tenant);
        changes = await ReceivedAsync(setup.Tenant, "/changes-access", 1);
        Assert.Equal([visible], changes.Select(c => c.Change.GetProperty("resourceData").GetProperty("id").GetGuid()));

        // Subscriptions are per user and per tenant.
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Admin.GetAsync($"/v1.0/changeSubscriptions/{id}", Ct)).StatusCode);
        await factory.CreateTenantAsync("changes-access-b");
        var other = await ApiClient.CreateAsync(factory, "changes-access-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/changeSubscriptions/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/changeSubscriptions/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/v1.0/changeSubscriptions",
            new { resource = setup.Resource, changeTypes = new[] { "created" }, notificationUrl = "https://hooks.example.test/intruder" }, Ct)).StatusCode);
    }
}
