using System.Net;

namespace PaperDotNet.IntegrationTests;

/// <summary>Every endpoint keeps tenants apart (no global query filters under AOT, ADR-0039).</summary>
public sealed class TenantIsolationTests : IAsyncLifetime
{
    private readonly TestHost _host = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Another_tenant_sees_and_changes_nothing()
    {
        var owner = await _host.SignInAsync();
        var workspace = await Api.CreateWorkspaceAsync(owner, "Private");
        var list = await Api.CreateListAsync(owner, workspace, "Private", new[] { new { name = "amount", type = "number" } });
        var item = await Api.CreateItemAsync(owner, workspace, list.Id(), new { title = "Secret", amount = 1 });
        var workspaceUri = $"/v1.0/workspaces/{workspace}";
        var listUri = $"{workspaceUri}/lists/{list.Id()}";
        var itemUri = $"{listUri}/items/{item.Id()}";

        var other = await _host.CreateTenantAsync("other");

        // The same user name exists in both tenants, each with its own data.
        var me = await (await other.GetAsync("/v1.0/me")).JsonAsync(HttpStatusCode.OK);
        Assert.NotEqual((await (await owner.GetAsync("/v1.0/me")).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId"), me.GetProperty("tenantId"));

        Assert.Empty((await (await other.GetAsync("/v1.0/workspaces")).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());
        Assert.DoesNotContain((await (await other.GetAsync("/v1.0/contentTypes")).JsonAsync(HttpStatusCode.OK)).EnumerateArray(),
            t => t.GetProperty("name").GetString() == "Private item");
        Assert.Single((await (await other.GetAsync("/v1.0/users")).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());
        Assert.Empty((await (await other.GetAsync("/v1.0/audit")).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());

        foreach (var response in new[]
        {
            await other.GetAsync(workspaceUri),
            await other.GetAsync($"{workspaceUri}/lists"),
            await other.PostAsJsonAsync($"{workspaceUri}/lists", new { name = "Injected" }),
            await other.GetAsync(listUri),
            await other.SendAsync(Api.Patch(listUri, new { name = "Taken" }, list.ETag())),
            await other.GetAsync($"{listUri}/items"),
            await other.PostAsJsonAsync($"{listUri}/items", new { fields = new { title = "Injected" } }),
            await other.GetAsync(itemUri),
            await other.SendAsync(Api.Patch(itemUri, new { fields = new { amount = 2 } }, item.ETag())),
            await other.DeleteAsync(itemUri),
            await other.DeleteAsync(listUri),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            response.Dispose();
        }

        var unchanged = await (await owner.GetAsync(itemUri)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(1, unchanged.GetProperty("fields").GetProperty("amount").GetInt32());
        Assert.Single((await (await owner.GetAsync($"{listUri}/items")).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());
    }
}
