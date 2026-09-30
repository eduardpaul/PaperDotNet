using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

public sealed class AuthTests : IAsyncLifetime
{
    private readonly TestHost _host = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Password_grant_issues_tokens_and_refresh_token_renews_them()
    {
        var client = _host.CreateClient();
        var tokens = await TestHost.RequestTokenAsync(client, new() { ["grant_type"] = "password", ["username"] = "admin", ["password"] = TestHost.AdminPassword });
        Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());
        Assert.Contains("user.manage", tokens.GetProperty("scope").GetString()!.Split(' '));

        var renewed = await TestHost.RequestTokenAsync(client, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()! });
        Assert.False(string.IsNullOrEmpty(renewed.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Wrong_password_and_forged_refresh_token_are_invalid_grants()
    {
        var client = _host.CreateClient();
        using var wrong = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = "admin", ["password"] = "nope-nope" }));
        Assert.Equal("invalid_grant", (await wrong.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("error").GetString());

        using var forged = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = "forged" }));
        Assert.Equal("invalid_grant", (await forged.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Users_of_a_suspended_tenant_cannot_sign_in_or_refresh()
    {
        await _host.CreateTenantAsync("paused");
        var client = _host.CreateClient();
        var form = new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = "admin", ["password"] = TestHost.AdminPassword, ["tenant"] = "paused" };
        var tokens = await TestHost.RequestTokenAsync(client, form);

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().SetStatusAsync("paused", TenantStatus.Suspended, TestContext.Current.CancellationToken);
        }

        using var password = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.Equal("invalid_grant", (await password.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("error").GetString());
        using var refresh = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()! }));
        Assert.Equal("invalid_grant", (await refresh.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Tokens_requested_with_scopes_are_limited_to_them()
    {
        var workspace = await Api.CreateWorkspaceAsync(await _host.SignInAsync(), "Scoped");
        var client = _host.CreateClient();
        var tokens = await TestHost.RequestTokenAsync(client, new() { ["grant_type"] = "password", ["username"] = "admin", ["password"] = TestHost.AdminPassword, ["scope"] = "list.read offline_access" });
        Assert.Equal("list.read", tokens.GetProperty("scope").GetString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1.0/workspaces/{workspace}/lists")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/v1.0/workspaces", new { name = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1.0/users")).StatusCode);

        var renewed = await TestHost.RequestTokenAsync(client, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()! });
        Assert.Equal("list.read", renewed.GetProperty("scope").GetString());
        using var unknown = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = "admin", ["password"] = TestHost.AdminPassword, ["scope"] = "no.such" }));
        Assert.Equal("invalid_scope", (await unknown.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_for_a_while()
    {
        var client = _host.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = "admin", ["password"] = "wrong-password" }));
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var locked = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = "admin", ["password"] = TestHost.AdminPassword }));
        Assert.Equal("invalid_grant", (await locked.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Api_tokens_are_limited_to_their_scopes_and_can_be_revoked()
    {
        var admin = await _host.SignInAsync();
        using var created = await admin.PostAsJsonAsync("/v1.0/me/apiTokens", new { name = "script", scopes = new[] { "list.read" } });
        var body = await created.JsonAsync(HttpStatusCode.Created);
        var secret = body.GetProperty("secret").GetString()!;
        var tokenId = body.GetProperty("token").Id();
        Assert.StartsWith("pdn_", secret);

        var workspace = await Api.CreateWorkspaceAsync(admin, "Scripts");
        var lists = $"/v1.0/workspaces/{workspace}/lists";
        var script = _host.CreateClient();
        script.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync(lists)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.PostAsJsonAsync("/v1.0/workspaces", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.PostAsJsonAsync("/v1.0/me/apiTokens", new { name = "more", scopes = new[] { "list.read" } })).StatusCode);
        Assert.Single((await (await admin.GetAsync("/v1.0/me/apiTokens")).JsonAsync(HttpStatusCode.OK)).EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/me/apiTokens/{tokenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync(lists)).StatusCode);
        var forged = _host.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new("Bearer", "pdn_forged-token-value");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync(lists)).StatusCode);
    }

    [Fact]
    public async Task Api_tokens_cannot_grant_scopes_the_user_does_not_hold()
    {
        var admin = await _host.SignInAsync();
        using (var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "limited", password = "limited-password-1" }))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var member = await _host.SignInAsync("limited", "limited-password-1");
        using var response = await member.PostAsJsonAsync("/v1.0/me/apiTokens", new { name = "escalate", scopes = new[] { "user.manage" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Requests_without_a_token_are_rejected()
    {
        using var response = await _host.CreateClient().GetAsync("/v1.0/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Members_have_the_scopes_of_the_member_role()
    {
        var admin = await _host.SignInAsync();
        using (var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "Member-Pass-1" }))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var member = await _host.SignInAsync("member", "Member-Pass-1");
        var me = await (await member.GetAsync("/v1.0/me")).JsonAsync(HttpStatusCode.OK);
        var scopes = me.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("list.write", scopes);
        Assert.DoesNotContain("user.manage", scopes);

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/v1.0/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/v1.0/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/v1.0/users", new { userName = "x", password = "Member-Pass-2" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/v1.0/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/v1.0/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/v1.0/contentTypes", new { name = "x" })).StatusCode);
    }
}
