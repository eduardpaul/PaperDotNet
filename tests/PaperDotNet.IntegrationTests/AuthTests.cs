using System.Net;

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
        Assert.Contains("users.manage", tokens.GetProperty("scope").GetString());

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
    public async Task Requests_without_a_token_are_rejected()
    {
        using var response = await _host.CreateClient().GetAsync("/v1.0/lists");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Members_have_list_scopes_only()
    {
        var admin = await _host.SignInAsync();
        using (var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "Member-Pass-1" }))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var member = await _host.SignInAsync("member", "Member-Pass-1");
        var me = await (await member.GetAsync("/v1.0/me")).JsonAsync(HttpStatusCode.OK);
        Assert.False(me.GetProperty("isAdmin").GetBoolean());

        using var users = await member.GetAsync("/v1.0/users");
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
        using var lists = await member.GetAsync("/v1.0/lists");
        Assert.Equal(HttpStatusCode.OK, lists.StatusCode);
    }
}
