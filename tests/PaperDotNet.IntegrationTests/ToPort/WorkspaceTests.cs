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
        stale.Headers.TryAddWithoutValidation("If-Match", "\"999\"");
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

    [Fact]
    public async Task Members_are_removed_but_the_last_owner_stays()
    {
        await factory.CreateTenantAsync("members-remove");
        await factory.CreateTenantAsync("members-remove-other");
        var admin = await ApiClient.CreateAsync(factory, "members-remove");
        var id = await admin.CreateWorkspaceAsync("Team");
        var ownerId = (await (await admin.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "helper", password = "helper-password-1" }, Ct);
        var helperId = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/v1.0/workspaces/{id}/members", new { userId = helperId, role = "member" }, Ct)).StatusCode);

        // Responses say what the caller may do there.
        var helper = await ApiClient.CreateAsync(factory, "members-remove", "helper", "helper-password-1");
        Assert.Equal("manage", (await (await admin.GetAsync($"/v1.0/workspaces/{id}", Ct)).ReadJsonAsync()).GetProperty("access").GetString());
        Assert.Equal("contribute", (await (await helper.GetAsync($"/v1.0/workspaces/{id}", Ct)).ReadJsonAsync()).GetProperty("access").GetString());
        var listed = (await (await helper.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(w => w.GetProperty("id").GetGuid() == id);
        Assert.Equal("contribute", listed.GetProperty("access").GetString());

        // A member cannot remove anyone; another tenant does not see the workspace.
        Assert.Equal(HttpStatusCode.NotFound, (await helper.DeleteAsync($"/v1.0/workspaces/{id}/members/{ownerId}", Ct)).StatusCode);
        var other = await ApiClient.CreateAsync(factory, "members-remove-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/workspaces/{id}/members/{helperId}", Ct)).StatusCode);

        // The only owner can be neither removed nor demoted.
        var removeOwner = await admin.DeleteAsync($"/v1.0/workspaces/{id}/members/{ownerId}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, removeOwner.StatusCode);
        Assert.Equal("lastOwner", (await removeOwner.ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/v1.0/workspaces/{id}/members", new { userId = ownerId, role = "member" }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/workspaces/{id}/members/{helperId}", Ct)).StatusCode);
        var members = (await (await admin.GetAsync($"/v1.0/workspaces/{id}/members", Ct)).ReadJsonAsync()).EnumerateArray()
            .Select(m => m.GetProperty("userId").GetGuid()).ToList();
        Assert.Equal([ownerId], members);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/v1.0/workspaces/{id}/members/{helperId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await helper.GetAsync($"/v1.0/workspaces/{id}", Ct)).StatusCode);
    }
}
