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
        var list = await Api.CreateListAsync(owner, "Private", new[] { new { name = "amount", type = "number" } });
        var item = await Api.CreateItemAsync(owner, list.Id(), new { title = "Secret", amount = 1 });
        var listUri = $"/v1.0/lists/{list.Id()}";
        var itemUri = $"{listUri}/items/{item.Id()}";

        var other = await _host.CreateTenantAsync("other");

        // The same user name exists in both tenants, each with its own data.
        var me = await (await other.GetAsync("/v1.0/me")).JsonAsync(HttpStatusCode.OK);
        Assert.NotEqual((await (await owner.GetAsync("/v1.0/me")).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId"), me.GetProperty("tenantId"));

        Assert.Empty((await (await other.GetAsync("/v1.0/lists")).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());
        Assert.Single((await (await other.GetAsync("/v1.0/users")).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());
        Assert.Empty((await (await other.GetAsync("/v1.0/audit")).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());

        foreach (var response in new[]
        {
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
