using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace PaperDotNet.IntegrationTests;

/// <summary>Sign-in through an authenticating reverse proxy (IAM-15, ADR-0031, ADR-0043).</summary>
public sealed class ReverseProxyTests(PaperDotNetApiFactory factory)
{
    /// <summary>The test factory trusts proxies in this network (<c>Auth:ReverseProxy:TrustedProxies</c>).</summary>
    public const string TrustedNetwork = "10.9.9.0/24";

    /// <summary>The proxy's secret in the test factory (<c>Auth:ReverseProxy:Secret</c>).</summary>
    public const string Secret = "integration-proxy-secret";

    /// <summary>The proxy's logout page in the test factory (<c>Auth:ReverseProxy:LogoutUrl</c>).</summary>
    public const string ProxyLogoutUrl = "https://proxy.example/logout";

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
            grantTypes = new[] { "authorization_code", "refresh_token" },
            scopes = new[] { "openid", "api", "offline_access" },
            redirectUris = new[] { Callback },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var clientId = (await created.ReadJsonAsync()).GetProperty("application").GetProperty("clientId").GetString()!;
        var (authorize, verifier) = AuthorizeUrl(clientId);
        return (clientId, authorize, verifier);
    }

    /// <summary>A new authorization request (fresh PKCE verifier) for the app.</summary>
    private static (string Url, string Verifier) AuthorizeUrl(string clientId)
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = $"/connect/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Callback)}" +
                  $"&scope={Uri.EscapeDataString("openid api offline_access")}&code_challenge={challenge}&code_challenge_method=S256&state=s1";
        return (url, verifier);
    }

    private static HttpRequestMessage Authorize(
        string url, string userName, string? name = null, string? email = null, string? groups = null, string? secret = Secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (secret is not null)
        {
            request.Headers.Add("X-PaperDotNet-Proxy", secret);
        }

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

    private static async Task<string> ExchangeAsync(HttpClient client, HttpResponseMessage redirect, string clientId, string verifier) =>
        (await TokensAsync(client, redirect, clientId, verifier)).GetProperty("access_token").GetString()!;

    private static async Task<JsonElement> TokensAsync(HttpClient client, HttpResponseMessage redirect, string clientId, string verifier)
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
        return await tokens.ReadJsonAsync();
    }

    private HttpClient WithToken(string tenant, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>The <c>Cookie</c> header value that sends back the cookies a response set.</summary>
    private static string Cookies(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

    /// <summary>Asserts a redirect to the proxy's sign-in path and returns its <c>returnUrl</c>.</summary>
    private static string ProxySignInRedirect(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/auth/proxy/sign-in?returnUrl=", location, StringComparison.Ordinal);
        return Uri.UnescapeDataString(location["/auth/proxy/sign-in?returnUrl=".Length..]);
    }

    [Fact]
    public async Task A_trusted_proxy_signs_users_in_and_creates_them()
    {
        await factory.CreateTenantAsync("proxy-auth");
        var admin = await ApiClient.CreateAsync(factory, "proxy-auth");
        var team = (await (await admin.PostAsJsonAsync("/v1.0/groups", new { name = "Team" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var (clientId, authorize, verifier) = await AppAsync(admin);

        // Headers from anyone but a trusted proxy are ignored: the sign-in goes to the proxy's path instead.
        var direct = From("proxy-auth", "203.0.113.7");
        ProxySignInRedirect(await direct.SendAsync(Authorize(authorize, "mallory"), Ct));

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
        ProxySignInRedirect(await From("proxy-auth", "10.9.9.3").SendAsync(Authorize(authorize, "hanna"), Ct));
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

    [Fact]
    public async Task The_proxy_signs_in_at_its_own_path_only_with_its_secret()
    {
        await factory.CreateTenantAsync("proxy-path");
        var admin = await ApiClient.CreateAsync(factory, "proxy-path");
        var (clientId, authorize, verifier) = await AppAsync(admin);
        var proxy = From("proxy-path", "10.9.9.2");

        // Signed out: /connect/authorize sends the browser to the proxy's path (outside /connect/).
        var returnUrl = ProxySignInRedirect(await proxy.GetAsync(authorize, Ct));
        Assert.Equal(authorize, returnUrl);
        var signIn = $"/auth/proxy/sign-in?returnUrl={Uri.EscapeDataString(returnUrl)}";

        // Without the secret, with a wrong one, or from elsewhere, the headers name nobody.
        Assert.Equal(HttpStatusCode.Unauthorized, (await proxy.SendAsync(Authorize(signIn, "mallory", secret: null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await proxy.SendAsync(Authorize(signIn, "mallory", secret: "integration-proxy-secreT"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await From("proxy-path", "203.0.113.7").SendAsync(Authorize(signIn, "mallory"), Ct)).StatusCode);
        ProxySignInRedirect(await proxy.SendAsync(Authorize(authorize, "mallory", secret: null), Ct));

        // With it: a sign-in session, then back to the authorization request, which needs no headers any more.
        var signedIn = await proxy.SendAsync(Authorize(signIn, "olga", "Olga Proxy"), Ct);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(authorize, signedIn.Headers.Location!.OriginalString);
        var continued = new HttpRequestMessage(HttpMethod.Get, authorize);
        continued.Headers.Add("Cookie", Cookies(signedIn));
        var tokens = await TokensAsync(proxy, await proxy.SendAsync(continued, Ct), clientId, verifier);

        // The proxy sign-in lasts 5 minutes in the test host: tokens never outlive it, even after a refresh.
        Assert.InRange(tokens.GetProperty("expires_in").GetInt32(), 1, 300);
        var refreshed = await proxy.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var again = await refreshed.ReadJsonAsync();
        Assert.InRange(again.GetProperty("expires_in").GetInt32(), 1, 300);
        var me = await (await WithToken("proxy-path", again.GetProperty("access_token").GetString()!).GetAsync("/v1.0/me", Ct)).ReadJsonAsync();
        Assert.Equal("Olga Proxy", me.GetProperty("displayName").GetString());

        // Only this server's authorization endpoint is continued (no open redirects).
        var elsewhere = await proxy.SendAsync(Authorize("/auth/proxy/sign-in?returnUrl=https%3A%2F%2Fevil.example%2F", "olga"), Ct);
        Assert.Equal("/", elsewhere.Headers.Location!.OriginalString);

        // Signing out of a proxy session goes on to the proxy's logout page.
        var logout = new HttpRequestMessage(HttpMethod.Get, "/connect/logout");
        logout.Headers.Add("Cookie", Cookies(signedIn));
        var loggedOut = await proxy.SendAsync(logout, Ct);
        Assert.Equal(HttpStatusCode.Redirect, loggedOut.StatusCode);
        Assert.Equal(ProxyLogoutUrl, loggedOut.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Group_sync_follows_the_proxy_but_keeps_local_groups_and_the_last_administrator()
    {
        await factory.CreateTenantAsync("proxy-groups");
        var admin = await ApiClient.CreateAsync(factory, "proxy-groups");
        var local = (await (await admin.PostAsJsonAsync("/v1.0/groups", new { name = "Local" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var (clientId, _, _) = await AppAsync(admin);
        var proxy = From("proxy-groups", "10.9.9.2");

        async Task<HttpClient> SignInAsync(string userName, string groups)
        {
            var (url, verifier) = AuthorizeUrl(clientId);
            return WithToken("proxy-groups", await ExchangeAsync(proxy, await proxy.SendAsync(Authorize(url, userName, groups: groups), Ct), clientId, verifier));
        }

        async Task<List<string>> GroupsOfAsync(HttpClient client, string userName)
        {
            var groups = (await (await client.GetAsync("/v1.0/groups", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
            var names = new List<string>();
            foreach (var group in groups)
            {
                var members = (await (await client.GetAsync($"/v1.0/groups/{group.GetProperty("id").GetGuid()}/members", Ct)).ReadJsonAsync()).EnumerateArray();
                if (members.Any(m => m.GetProperty("userName").GetString() == userName))
                {
                    names.Add(group.GetProperty("name").GetString()!);
                }
            }

            return [.. names.Order(StringComparer.Ordinal)];
        }

        // Missing groups are created as proxy groups; existing local groups are joined.
        await SignInAsync("pia", "Ops, Local");
        Assert.Equal(["Local", "Ops"], await GroupsOfAsync(admin, "pia"));

        // The proxy stops naming them: pia leaves the proxy group, but stays in the local one.
        await SignInAsync("pia", "");
        Assert.Equal(["Local"], await GroupsOfAsync(admin, "pia"));
        Assert.Contains(local, (await (await admin.GetAsync("/v1.0/groups", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Select(g => g.GetProperty("id").GetGuid()));

        // boss is the only administrator, through the proxy group Bosses: dropping it would leave none, so it stays.
        var boss = await SignInAsync("boss", "Bosses");
        var bosses = (await (await admin.GetAsync("/v1.0/groups", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Single(g => g.GetProperty("name").GetString() == "Bosses").GetProperty("id").GetGuid();
        var roles = (await (await admin.GetAsync("/v1.0/roles", Ct)).ReadJsonAsync()).EnumerateArray().ToList();
        var administrator = roles.Single(r => r.GetProperty("grantsAllScopes").GetBoolean()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created,
            (await admin.PostAsJsonAsync($"/v1.0/roles/{administrator}/assignments", new { principalId = bosses, principalType = "group" }, Ct)).StatusCode);
        var adminId = (await (await admin.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var assignments = (await (await admin.GetAsync($"/v1.0/roles/{administrator}/assignments", Ct)).ReadJsonAsync()).EnumerateArray();
        var adminAssignment = assignments.Single(a => a.GetProperty("principalId").GetGuid() == adminId).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/roles/{administrator}/assignments/{adminAssignment}", Ct)).StatusCode);

        boss = await SignInAsync("boss", "");
        Assert.Equal(["Bosses"], await GroupsOfAsync(boss, "boss"));
    }

    [Fact]
    public async Task Live_events_pass_through_buffering_proxies()
    {
        await factory.CreateTenantAsync("proxy-stream");
        var client = await ApiClient.CreateAsync(factory, "proxy-stream");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me/events");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());

        // A quiet stream sends keep-alives (every second in the test host), so proxies do not end it.
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var types = new List<string>();
        while (types.Count(t => t == "keepalive") < 2 && await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                types.Add(line[7..]);
            }
        }

        Assert.Equal(["connected", "keepalive", "keepalive"], types);
    }

    [Fact]
    public void Forwarded_headers_count_only_from_known_proxies()
    {
        static Microsoft.AspNetCore.Builder.ForwardedHeadersOptions Configure(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
            PaperDotNet.Host.ReverseProxySetup.ConfigureForwardedHeaders(options, configuration);
            return options;
        }

        // The sign-in proxies are the default; ForwardedHeaders:KnownProxies replaces them.
        var fromSignIn = Configure(new() { ["Auth:ReverseProxy:TrustedProxies:0"] = "172.30.10.2" });
        Assert.Equal([IPAddress.Parse("172.30.10.2")], fromSignIn.KnownProxies);
        Assert.Empty(fromSignIn.KnownIPNetworks);
        Assert.Equal(1, fromSignIn.ForwardLimit);

        var known = Configure(new()
        {
            ["Auth:ReverseProxy:TrustedProxies:0"] = "172.30.10.2",
            ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.0/8",
            ["ForwardedHeaders:ForwardLimit"] = "2",
        });
        Assert.Empty(known.KnownProxies);
        Assert.Equal("10.0.0.0/8", Assert.Single(known.KnownIPNetworks).ToString());
        Assert.Equal(2, known.ForwardLimit);

        Assert.Throws<InvalidOperationException>(() => Configure(new() { ["ForwardedHeaders:KnownProxies:0"] = "npm" }));
    }
}
