using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Identity.Authentication;

namespace PaperDotNet.IntegrationTests;

/// <summary>Tests that change host-wide settings; they run alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostSettings
{
    public const string Name = "Host settings";
}

/// <summary>Turning local sign-in off leaves the proxy as the only interactive way in (ADR-0043).</summary>
[Collection(HostSettings.Name)]
public sealed class LocalSignInTests(PaperDotNetApiFactory factory)
{
    private const string Callback = "https://app.example/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_local_sign_in_only_the_proxy_signs_people_in()
    {
        await factory.CreateTenantAsync("local-off");
        var admin = await ApiClient.CreateAsync(factory, "local-off");
        var created = await admin.PostAsJsonAsync("/v1.0/applications", new
        {
            displayName = "Web UI",
            clientType = "public",
            grantTypes = new[] { "authorization_code" },
            scopes = new[] { "openid", "api" },
            redirectUris = new[] { Callback },
        }, Ct);
        var clientId = (await created.ReadJsonAsync()).GetProperty("application").GetProperty("clientId").GetString()!;
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)))));
        var authorize = $"/connect/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Callback)}" +
                        $"&scope=openid%20api&code_challenge={challenge}&code_challenge_method=S256&state=s1";

        // A password session from before the switch.
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        browser.DefaultRequestHeaders.Add("X-Tenant", "local-off");
        var login = new { userName = PaperDotNetApiFactory.AdminUserName, password = PaperDotNetApiFactory.AdminPassword };
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsJsonAsync("/v1.0/auth/login", login, Ct)).StatusCode);

        var options = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        options.LocalSignIn = false;
        try
        {
            // Passwords and passkeys are refused, by the session sign-in and by the password grant.
            var refused = await browser.PostAsJsonAsync("/v1.0/auth/login", login, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("localSignInDisabled", (await refused.ReadJsonAsync()).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsJsonAsync("/v1.0/auth/passkeys/options", new { }, Ct)).StatusCode);
            var anonymous = await ApiClient.CreateAsync(factory, "local-off", userName: null);
            Assert.False((await ApiClient.RequestTokenAsync(anonymous, PaperDotNetApiFactory.AdminUserName, PaperDotNetApiFactory.AdminPassword)).IsSuccessStatusCode);

            // The earlier password session no longer counts: the authorization goes to the proxy.
            var signedOut = await browser.GetAsync(authorize, Ct);
            Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
            Assert.StartsWith("/auth/proxy/sign-in?returnUrl=", signedOut.Headers.Location!.OriginalString, StringComparison.Ordinal);

            // ... which still signs people in, and tokens issued before keep working.
            var proxy = factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = IPAddress.Parse("10.9.9.2"));
            using var fromProxy = new HttpClient(proxy) { BaseAddress = factory.Server.BaseAddress };
            fromProxy.DefaultRequestHeaders.Add("X-Tenant", "local-off");
            using var signIn = new HttpRequestMessage(HttpMethod.Get, signedOut.Headers.Location);
            signIn.Headers.Add("X-PaperDotNet-Proxy", ReverseProxyTests.Secret);
            signIn.Headers.Add("Remote-User", "rita");
            var session = await fromProxy.SendAsync(signIn, Ct);
            Assert.Equal(HttpStatusCode.Redirect, session.StatusCode);
            using var continued = new HttpRequestMessage(HttpMethod.Get, authorize);
            continued.Headers.Add("Cookie", string.Join("; ", session.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
            var code = await fromProxy.SendAsync(continued, Ct);
            Assert.Equal(HttpStatusCode.Redirect, code.StatusCode);
            Assert.StartsWith(Callback, code.Headers.Location!.OriginalString, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/v1.0/me", Ct)).StatusCode);
        }
        finally
        {
            options.LocalSignIn = true;
        }
    }
}
