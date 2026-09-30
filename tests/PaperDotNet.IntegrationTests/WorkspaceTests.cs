using System.Net;

namespace PaperDotNet.IntegrationTests;

public sealed class WorkspaceTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<Guid> CreateAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/v1.0/workspaces", new { name }, Ct);
        return Guid.Parse((await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private async Task<Guid> UserAsync(string userName)
    {
        using var response = await _admin.PostAsJsonAsync("/v1.0/users", new { userName, password = $"{userName}-password-1" }, Ct);
        return Guid.Parse((await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private static HttpRequestMessage WithETag(HttpMethod method, string uri, object? body, string? etag)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return request;
    }

    [Fact]
    public async Task Update_requires_a_matching_etag()
    {
        var id = await CreateAsync(_admin, "Concurrency");
        var etag = (await _admin.GetAsync($"/v1.0/workspaces/{id}", Ct)).Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await _admin.SendAsync(WithETag(HttpMethod.Patch, $"/v1.0/workspaces/{id}", new { name = "Renamed" }, null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await _admin.SendAsync(WithETag(HttpMethod.Patch, $"/v1.0/workspaces/{id}", new { name = "Renamed" }, "\"999\""), Ct)).StatusCode);
        using var updated = await _admin.SendAsync(WithETag(HttpMethod.Patch, $"/v1.0/workspaces/{id}", new { name = "Renamed" }, etag), Ct);
        Assert.Equal("Renamed", (await updated.JsonAsync(HttpStatusCode.OK)).GetProperty("name").GetString());
        Assert.NotEqual(etag, updated.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Lists_are_paged_with_next_links()
    {
        for (var i = 0; i < 3; i++)
        {
            await CreateAsync(_admin, $"Workspace {i}");
        }

        var first = await (await _admin.GetAsync("/v1.0/workspaces?$top=2", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(2, first.GetProperty("value").GetArrayLength());
        var next = first.GetProperty("@odata.nextLink").GetString()!;
        var second = await (await _admin.GetAsync(new Uri(next).PathAndQuery, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(1, second.GetProperty("value").GetArrayLength());
        Assert.False(second.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task Deleted_workspaces_disappear()
    {
        var id = await CreateAsync(_admin, "Temporary");
        var etag = (await _admin.GetAsync($"/v1.0/workspaces/{id}", Ct)).Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.SendAsync(WithETag(HttpMethod.Delete, $"/v1.0/workspaces/{id}", null, etag), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/v1.0/workspaces/{id}", Ct)).StatusCode);
        Assert.DoesNotContain((await (await _admin.GetAsync("/v1.0/workspaces", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray(), w => Guid.Parse(w.Id()) == id);
    }

    [Fact]
    public async Task Members_only_see_their_own_workspaces()
    {
        var hidden = await CreateAsync(_admin, "Admin only");
        await UserAsync("viewer");
        var member = await _host.SignInAsync("viewer", "viewer-password-1");
        var own = await CreateAsync(member, "Mine");

        var visible = (await (await member.GetAsync("/v1.0/workspaces", Ct)).JsonAsync(HttpStatusCode.OK))
            .GetProperty("value").EnumerateArray().Select(w => Guid.Parse(w.Id())).ToList();

        Assert.Contains(own, visible);
        Assert.DoesNotContain(hidden, visible);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/v1.0/workspaces/{hidden}", Ct)).StatusCode);

        // Administrators (workspace.manage) see and manage every workspace.
        Assert.Equal("manage", (await (await _admin.GetAsync($"/v1.0/workspaces/{own}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("access").GetString());
    }

    [Fact]
    public async Task Members_are_removed_but_the_last_owner_stays()
    {
        var id = await CreateAsync(_admin, "Team");
        var ownerId = Guid.Parse((await (await _admin.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).Id());
        var helperId = await UserAsync("helper");
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/workspaces/{id}/members", new { userId = helperId, role = "member" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync($"/v1.0/workspaces/{id}/members", new { userId = helperId, role = "boss" }, Ct)).StatusCode);

        // Responses say what the caller may do there.
        var helper = await _host.SignInAsync("helper", "helper-password-1");
        Assert.Equal("manage", (await (await _admin.GetAsync($"/v1.0/workspaces/{id}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("access").GetString());
        Assert.Equal("contribute", (await (await helper.GetAsync($"/v1.0/workspaces/{id}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("access").GetString());
        var listed = (await (await helper.GetAsync("/v1.0/workspaces", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
            .Single(w => Guid.Parse(w.Id()) == id);
        Assert.Equal("contribute", listed.GetProperty("access").GetString());

        // A member cannot remove anyone; another tenant does not see the workspace.
        Assert.Equal(HttpStatusCode.NotFound, (await helper.DeleteAsync($"/v1.0/workspaces/{id}/members/{ownerId}", Ct)).StatusCode);
        var other = await _host.CreateTenantAsync("members-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/workspaces/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/workspaces/{id}/members", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/v1.0/workspaces/{id}/members/{helperId}", Ct)).StatusCode);
        Assert.Empty((await (await other.GetAsync("/v1.0/workspaces", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());

        // The only owner can be neither removed nor demoted.
        using var removeOwner = await _admin.DeleteAsync($"/v1.0/workspaces/{id}/members/{ownerId}", Ct);
        Assert.Equal("lastOwner", (await removeOwner.JsonAsync(HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsJsonAsync($"/v1.0/workspaces/{id}/members", new { userId = ownerId, role = "member" }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/workspaces/{id}/members/{helperId}", Ct)).StatusCode);
        var members = (await (await _admin.GetAsync($"/v1.0/workspaces/{id}/members", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
            .Select(m => m.GetProperty("userId").GetGuid()).ToList();
        Assert.Equal([ownerId], members);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"/v1.0/workspaces/{id}/members/{helperId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await helper.GetAsync($"/v1.0/workspaces/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Deleted_users_leave_their_workspaces()
    {
        var id = await CreateAsync(_admin, "Team");
        var carol = await UserAsync("carol");
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/workspaces/{id}/members", new { userId = carol, role = "visitor" }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/v1.0/users/{carol}", Ct)).StatusCode);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var members = (await (await _admin.GetAsync($"/v1.0/workspaces/{id}/members", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray();
            if (members.All(m => m.GetProperty("userId").GetGuid() != carol))
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail("The deleted user is still a member.");
    }
}
