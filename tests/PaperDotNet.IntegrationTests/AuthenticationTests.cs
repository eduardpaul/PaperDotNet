using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace PaperDotNet.IntegrationTests;

public sealed class AuthenticationTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Api_requires_authentication()
    {
        var client = await ApiClient.CreateAsync(factory, userName: null);

        var response = await client.GetAsync("/v1.0/workspaces", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_with_a_problem()
    {
        var client = await ApiClient.CreateAsync(factory, userName: null);

        var response = await client.PostAsJsonAsync("/v1.0/auth/token", new { userName = "admin", password = "wrong-password-123" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalidCredentials", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Me_returns_the_signed_in_user_and_scopes()
    {
        var client = await ApiClient.CreateAsync(factory);

        var me = await (await client.GetAsync("/v1.0/me", Ct)).ReadJsonAsync();

        Assert.Equal("admin", me.GetProperty("userName").GetString());
        Assert.Equal("default", me.GetProperty("tenantIdentifier").GetString());
        var scopes = me.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("workspace.manage", scopes);
        Assert.Contains("user.manage", scopes);
    }

    [Fact]
    public async Task Api_token_is_limited_to_its_scopes_and_can_be_revoked()
    {
        var admin = await ApiClient.CreateAsync(factory);
        var created = await admin.PostAsJsonAsync("/v1.0/me/apiTokens", new { name = "script", scopes = new[] { "workspace.read" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.ReadJsonAsync();
        var secret = body.GetProperty("secret").GetString()!;
        var tokenId = body.GetProperty("token").GetProperty("id").GetGuid();

        var script = factory.CreateClient();
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.PostAsJsonAsync("/v1.0/workspaces", new { name = "Nope" }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/me/apiTokens/{tokenId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
    }

    [Fact]
    public async Task Api_token_cannot_grant_scopes_the_user_does_not_hold()
    {
        var admin = await ApiClient.CreateAsync(factory);
        var createUser = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "limited", password = "limited-password-1" }, Ct);
        Assert.Equal(HttpStatusCode.Created, createUser.StatusCode);
        var member = await ApiClient.CreateAsync(factory, userName: "limited", password: "limited-password-1");

        var response = await member.PostAsJsonAsync("/v1.0/me/apiTokens", new { name = "escalate", scopes = new[] { "user.manage" } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Members_cannot_use_admin_endpoints()
    {
        var admin = await ApiClient.CreateAsync(factory);
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "member1", password = "member-password-1" }, Ct);
        var member = await ApiClient.CreateAsync(factory, userName: "member1", password: "member-password-1");

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/v1.0/users", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/v1.0/users", new { userName = "x", password = "some-password-1" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/v1.0/roles", Ct)).StatusCode);
    }
}
