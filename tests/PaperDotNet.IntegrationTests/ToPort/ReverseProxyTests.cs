using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Sign-in through an authenticating reverse proxy (IAM-15).</summary>
public sealed class ReverseProxyTests(PaperDotNetApiFactory factory)
{
    /// <summary>The test factory trusts proxies in this network (<c>Auth:ReverseProxy:TrustedProxies</c>).</summary>
    public const string TrustedNetwork = "10.9.9.0/24";

    private const string Callback = "https://app.example/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A client whose requests come from <paramref name="peer"/> (the proxy's address), without following redirects.</summary>
    private HttpClient From(string tenant, string peer)
    {
        var handler = factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = IPAddress.Parse(peer));
        var client = new HttpClient(handler) { BaseAddress = factory.Server.BaseAddress };
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private static async Task<(string ClientId, string Authorize, string Verifier)> AppAsync(HttpClient admin)
    {
        var created = await admin.PostAsJsonAsync("/v1.0/applications", new
        {
            displayName = "Web UI",
            clientType = "public",
            grantTypes = new[] { "authorization_code" },
            scopes = new[] { "openid", "api" },
            redirectUris = new[] { Callback },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var clientId = (await created.ReadJsonAsync()).GetProperty("application").GetProperty("clientId").GetString()!;
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = $"/connect/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Callback)}" +
                        $"&scope={Uri.EscapeDataString("openid api")}&code_challenge={challenge}&code_challenge_method=S256&state=s1";
        return (clientId, authorize, verifier);
    }

    private static HttpRequestMessage Authorize(string url, string userName, string? name = null, string? email = null, string? groups = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Remote-User", userName);
        if (name is not null)
        {
            request.Headers.Add("Remote-Name", name);
        }

        if (email is not null)
        {
            request.Headers.Add("Remote-Email", email);
        }

        if (groups is not null)
        {
            request.Headers.Add("Remote-Groups", groups);
        }

        return request;
    }

    private static async Task<string> ExchangeAsync(HttpClient client, HttpResponseMessage redirect, string clientId, string verifier)
    {
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var code = System.Web.HttpUtility.ParseQueryString(redirect.Headers.Location!.Query)["code"]!;
        var tokens = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = verifier,
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
        return (await tokens.ReadJsonAsync()).GetProperty("access_token").GetString()!;
    }

    [Fact]
    public async Task A_trusted_proxy_signs_users_in_and_creates_them()
    {
        await factory.CreateTenantAsync("proxy-auth");
        var admin = await ApiClient.CreateAsync(factory, "proxy-auth");
        var team = (await (await admin.PostAsJsonAsync("/v1.0/groups", new { name = "Team" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var (clientId, authorize, verifier) = await AppAsync(admin);

        // Headers from anyone but a trusted proxy are ignored.
        var direct = From("proxy-auth", "203.0.113.7");
        Assert.Equal(HttpStatusCode.Unauthorized, (await direct.SendAsync(Authorize(authorize, "mallory"), Ct)).StatusCode);

        var proxy = From("proxy-auth", "10.9.9.2");
        var redirect = await proxy.SendAsync(Authorize(authorize, "hanna", "Hanna Proxy", "hanna@example.com", "Team, Nonexistent"), Ct);
        var token = await ExchangeAsync(proxy, redirect, clientId, verifier);

        var hanna = factory.CreateClient();
        hanna.DefaultRequestHeaders.Add("X-Tenant", "proxy-auth");
        hanna.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await (await hanna.GetAsync("/v1.0/me", Ct)).ReadJsonAsync();
        Assert.Equal("hanna", me.GetProperty("userName").GetString());
        Assert.Equal("Hanna Proxy", me.GetProperty("displayName").GetString());
        Assert.Equal("hanna@example.com", me.GetProperty("email").GetString());
        var members = (await (await admin.GetAsync($"/v1.0/groups/{team}/members", Ct)).ReadJsonAsync()).EnumerateArray();
        Assert.Contains(members, m => m.GetProperty("userName").GetString() == "hanna");
        Assert.Equal(HttpStatusCode.OK, (await hanna.GetAsync("/v1.0/workspaces", Ct)).StatusCode);

        // Users created by the proxy have no password, and disabled users cannot sign in through it.
        var anonymous = await ApiClient.CreateAsync(factory, "proxy-auth", userName: null);
        Assert.False((await ApiClient.RequestTokenAsync(anonymous, "hanna", "")).IsSuccessStatusCode);
        var id = me.GetProperty("id").GetGuid();
        var disable = new HttpRequestMessage(HttpMethod.Patch, $"/v1.0/users/{id}") { Content = JsonContent.Create(new { isDisabled = true }) };
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(disable, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await From("proxy-auth", "10.9.9.3").SendAsync(Authorize(authorize, "hanna"), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_proxy_identity_stays_in_its_tenant()
    {
        await factory.CreateTenantAsync("proxy-iso-a");
        await factory.CreateTenantAsync("proxy-iso-b");
        var a = await ApiClient.CreateAsync(factory, "proxy-iso-a");
        await a.PostAsJsonAsync("/v1.0/users", new { userName = "ivan", password = "ivan-password-1" }, Ct);
        var b = await ApiClient.CreateAsync(factory, "proxy-iso-b");
        var (clientId, authorize, verifier) = await AppAsync(b);

        // "ivan" of tenant A is a different, new user in tenant B.
        var proxy = From("proxy-iso-b", "10.9.9.2");
        var token = await ExchangeAsync(proxy, await proxy.SendAsync(Authorize(authorize, "ivan"), Ct), clientId, verifier);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", "proxy-iso-b");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await (await client.GetAsync("/v1.0/me", Ct)).ReadJsonAsync();
        var usersA = (await (await a.GetAsync("/v1.0/users", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray();
        Assert.DoesNotContain(usersA, u => u.GetProperty("id").GetGuid() == me.GetProperty("id").GetGuid());
        Assert.Equal("proxy-iso-b", me.GetProperty("tenantIdentifier").GetString());
    }
}
