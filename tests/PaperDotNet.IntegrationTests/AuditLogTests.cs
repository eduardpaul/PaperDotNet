using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>The audit log of every module (LST-14): written by the save guard in the transaction of the change.</summary>
public sealed class AuditLogTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<List<JsonElement>> LogAsync(HttpClient client, string query) =>
        [.. (await (await client.GetAsync($"/v1.0/auditLog?{query}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()];

    [Fact]
    public async Task Changes_of_every_module_are_recorded_with_who_what_and_when()
    {
        var me = (await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id();
        var ws = await Api.CreateWorkspaceAsync(_admin, "Audited");
        var list = (await Api.CreateListAsync(_admin, ws, "Tasks", new[] { new { name = "state", type = "text" } })).Id();
        var item = await Api.CreateItemAsync(_admin, ws, list, new { title = "Draft", state = "open" });
        using var update = await _admin.SendAsync(Api.Patch($"{Api.Items(ws, list)}/{item.Id()}", new { fields = new { state = "done" } }, item.ETag()), Ct);
        var updated = await update.JsonAsync(HttpStatusCode.OK);
        using var delete = await _admin.SendAsync(Api.WithETag(HttpMethod.Delete, $"{Api.Items(ws, list)}/{item.Id()}", null, updated.ETag()), Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var changes = await LogAsync(_admin, $"entityId={item.Id()}");
        Assert.Equal(["deleted", "updated", "created"], changes.Select(c => c.GetProperty("action").GetString()));
        Assert.All(changes, c => Assert.Equal("lists.ListItem", c.GetProperty("entityType").GetString()));
        Assert.All(changes, c => Assert.Equal(me, c.GetProperty("userId").GetString()));
        Assert.NotEmpty(changes[1].GetProperty("properties").EnumerateArray());
        Assert.DoesNotContain("Version", changes[1].GetProperty("properties").EnumerateArray().Select(p => p.GetString()));
        Assert.True(changes[0].GetProperty("at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));

        // Other modules, filtered by entity type and user; technical rows (item versions, values) are left out.
        var workspaces = await LogAsync(_admin, "entityType=workspaces.Workspace");
        Assert.Contains(workspaces, c => c.GetProperty("entityId").GetString() == ws && c.GetProperty("action").GetString() == "created");
        Assert.NotEmpty(await LogAsync(_admin, $"entityType=lists.ListDefinition&userId={me}"));
        Assert.Empty(await LogAsync(_admin, "entityType=lists.ItemVersion"));
        Assert.Empty(await LogAsync(_admin, "entityType=lists.ItemValue"));
        Assert.Empty(await LogAsync(_admin, $"entityId={item.Id()}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"))}"));
        Assert.Equal(3, (await LogAsync(_admin, $"entityId={item.Id()}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"))}")).Count);
    }

    [Fact]
    public async Task The_log_pages_and_stays_in_the_tenant()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Paged");
        var list = (await Api.CreateListAsync(_admin, ws, "Notes")).Id();
        await Api.CreateItemAsync(_admin, ws, list, new { title = "one" });
        await Api.CreateItemAsync(_admin, ws, list, new { title = "two" });

        var first = await (await _admin.GetAsync("/v1.0/auditLog?entityType=lists.ListItem&$top=1", Ct)).JsonAsync(HttpStatusCode.OK);
        var next = first.GetProperty("@odata.nextLink").GetString()!;
        var second = await (await _admin.GetAsync(next, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.NotEqual(first.GetProperty("value")[0].Id(), second.GetProperty("value")[0].Id());

        var other = await _host.CreateTenantAsync("audit-other");
        Assert.Empty(await LogAsync(other, "entityType=lists.ListItem"));
        Assert.DoesNotContain(await LogAsync(other, "entityType=workspaces.Workspace"), c => c.GetProperty("entityId").GetString() == ws);
    }
}
