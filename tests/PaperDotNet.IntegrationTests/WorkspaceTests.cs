using System.Net;
using System.Net.Http.Json;

namespace PaperDotNet.IntegrationTests;

public sealed class WorkspaceTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Update_requires_a_matching_etag()
    {
        var client = await ApiClient.CreateAsync(factory);
        var id = await client.CreateWorkspaceAsync("Concurrency");
        var get = await client.GetAsync($"/v1.0/workspaces/{id}", Ct);
        var etag = get.Headers.ETag!.Tag;

        var withoutEtag = await client.PatchAsJsonAsync($"/v1.0/workspaces/{id}", new { name = "Renamed" }, Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutEtag.StatusCode);

        var stale = new HttpRequestMessage(HttpMethod.Patch, $"/v1.0/workspaces/{id}") { Content = JsonContent.Create(new { name = "Renamed" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale, Ct)).StatusCode);

        var current = new HttpRequestMessage(HttpMethod.Patch, $"/v1.0/workspaces/{id}") { Content = JsonContent.Create(new { name = "Renamed" }) };
        current.Headers.TryAddWithoutValidation("If-Match", etag);
        var updated = await client.SendAsync(current, Ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Renamed", (await updated.ReadJsonAsync()).GetProperty("name").GetString());
        Assert.NotEqual(etag, updated.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Lists_are_paged_with_next_links()
    {
        var tenant = await factory.CreateTenantAsync("paging");
        var client = await ApiClient.CreateAsync(factory, tenant.Identifier);
        for (var i = 0; i < 3; i++)
        {
            await client.CreateWorkspaceAsync($"Workspace {i}");
        }

        var first = await (await client.GetAsync("/v1.0/workspaces?$top=2", Ct)).ReadJsonAsync();
        Assert.Equal(2, first.GetProperty("value").GetArrayLength());
        var next = first.GetProperty("@odata.nextLink").GetString()!;

        var second = await (await client.GetAsync(new Uri(next).PathAndQuery, Ct)).ReadJsonAsync();
        Assert.Equal(1, second.GetProperty("value").GetArrayLength());
        Assert.False(second.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task Deleted_workspaces_disappear()
    {
        var client = await ApiClient.CreateAsync(factory);
        var id = await client.CreateWorkspaceAsync("Temporary");
        var etag = (await client.GetAsync($"/v1.0/workspaces/{id}", Ct)).Headers.ETag!.Tag;

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/v1.0/workspaces/{id}");
        delete.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1.0/workspaces/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Members_only_see_their_own_workspaces()
    {
        var admin = await ApiClient.CreateAsync(factory);
        var hidden = await admin.CreateWorkspaceAsync("Admin only");
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "viewer", password = "viewer-password-1" }, Ct);
        var member = await ApiClient.CreateAsync(factory, userName: "viewer", password: "viewer-password-1");
        var own = await member.CreateWorkspaceAsync("Mine");

        var visible = (await (await member.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync())
            .GetProperty("value").EnumerateArray().Select(w => w.GetProperty("id").GetGuid()).ToList();

        Assert.Contains(own, visible);
        Assert.DoesNotContain(hidden, visible);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/v1.0/workspaces/{hidden}", Ct)).StatusCode);
    }
}
