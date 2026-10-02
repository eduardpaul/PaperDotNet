using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.IntegrationTests;

/// <summary>OAuth 2.0 / OpenID Connect (IAM-02) and sign-in sessions (IAM-01).</summary>
public sealed class OAuthTests : IAsyncLifetime
{
    private const string Callback = "https://app.example/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new(settings: new Dictionary<string, string> { ["Tenancy:AllowHeader"] = "true" });

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>A browser-like client of a tenant: keeps cookies, does not follow redirects.</summary>
    private HttpClient Browser(string tenant)
    {
        var client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private static Task<HttpResponseMessage> TokenAsync(HttpClient client, Dictionary<string, string> form) =>
        client.PostAsync("/connect/token", new FormUrlEncodedContent(form), Ct);

    private static HttpClient WithToken(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> CreateApplicationAsync(HttpClient admin, object request)
    {
        using var response = await admin.PostAsJsonAsync("/v1.0/applications", request, Ct);
        return await response.JsonAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Discovery_document_describes_the_server()
    {
        var client = Browser("default");

        var discovery = await (await client.GetAsync("/.well-known/openid-configuration", Ct)).JsonAsync(HttpStatusCode.OK);
        var jwks = await (await client.GetAsync(discovery.GetProperty("jwks_uri").GetString(), Ct)).JsonAsync(HttpStatusCode.OK);

        Assert.EndsWith("/connect/token", discovery.GetProperty("token_endpoint").GetString(), StringComparison.Ordinal);
        Assert.Contains("S256", discovery.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(m => m.GetString()));
        var key = Assert.Single(jwks.GetProperty("keys").EnumerateArray());
        Assert.Equal("RSA", key.GetProperty("kty").GetString());

        // The key is stored once (private part protected) and shared by every request.
        await using var scope = _host.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().ServerKeys.CountAsync(Ct));
    }

    [Fact]
    public async Task Password_and_refresh_grants_issue_working_tokens()
    {
        await _host.CreateTenantAsync("oauth-password");
        var client = Browser("oauth-password");

        using var first = await TokenAsync(client, new()
        {
            ["grant_type"] = "password",
            ["client_id"] = "paperdotnet",
            ["username"] = "admin",
            ["password"] = TestHost.AdminPassword,
        });
        var tokens = await first.JsonAsync(HttpStatusCode.OK);
        using var refreshed = await TokenAsync(client, new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "paperdotnet",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        });
        var access = (await refreshed.JsonAsync(HttpStatusCode.OK)).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await WithToken(Browser("oauth-password"), access).GetAsync("/v1.0/me", Ct)).StatusCode);

        using var wrong = await TokenAsync(client, new() { ["grant_type"] = "password", ["username"] = "admin", ["password"] = "wrong-password-123" });
        Assert.Equal("invalid_grant", (await wrong.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Authorization_code_flow_with_pkce_uses_the_sign_in_session()
    {
        var admin = await _host.CreateTenantAsync("oauth-code");
        var app = await CreateApplicationAsync(admin, new
        {
            displayName = "Notes app",
            clientType = "public",
            grantTypes = new[] { "authorization_code", "refresh_token" },
            scopes = new[] { "openid", "offline_access", "workspace.read" },
            redirectUris = new[] { Callback },
        });
        var clientId = app.GetProperty("application").GetProperty("clientId").GetString()!;
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = $"/connect/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Callback)}" +
                        $"&scope={Uri.EscapeDataString("openid offline_access workspace.read")}&code_challenge={challenge}&code_challenge_method=S256&state=xyz&nonce=n-1";

        var browser = Browser("oauth-code");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(authorize, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/v1.0/auth/login", new { userName = "admin", password = "wrong-password-1" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsJsonAsync("/v1.0/auth/login", new { userName = "admin", password = TestHost.AdminPassword }, Ct)).StatusCode);

        // Without PKCE, or with a scope the client may not ask for: an error back at the client.
        var noPkce = await browser.GetAsync(authorize.Replace("&code_challenge_method=S256", "", StringComparison.Ordinal).Replace($"&code_challenge={challenge}", "", StringComparison.Ordinal), Ct);
        Assert.Contains("error=invalid_request", noPkce.Headers.Location!.ToString(), StringComparison.Ordinal);
        var badScope = await browser.GetAsync(authorize.Replace("workspace.read", "user.manage", StringComparison.Ordinal), Ct);
        Assert.Contains("error=invalid_scope", badScope.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync(authorize.Replace(Uri.EscapeDataString(Callback), Uri.EscapeDataString("https://evil.example/cb"), StringComparison.Ordinal), Ct)).StatusCode);

        var redirect = await browser.GetAsync(authorize, Ct);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var location = redirect.Headers.Location!;
        Assert.StartsWith(Callback, location.ToString(), StringComparison.Ordinal);
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("xyz", query["state"]);

        Dictionary<string, string> Exchange(string codeVerifier) => new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = query["code"]!,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = codeVerifier,
        };

        // A wrong verifier fails, and uses the code up.
        using (var wrongVerifier = await TokenAsync(Browser("oauth-code"), Exchange(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongVerifier.StatusCode);
        }

        var second = await browser.GetAsync(authorize, Ct);
        query = System.Web.HttpUtility.ParseQueryString(second.Headers.Location!.Query);
        using var exchange = await TokenAsync(Browser("oauth-code"), Exchange(verifier));
        var tokens = await exchange.JsonAsync(HttpStatusCode.OK);
        Assert.True(tokens.TryGetProperty("refresh_token", out _));

        // The identity token is signed with the published key, for this client, with the nonce.
        var jwks = await (await browser.GetAsync("/.well-known/jwks", Ct)).Content.ReadAsStringAsync(Ct);
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(tokens.GetProperty("id_token").GetString(), new TokenValidationParameters
        {
            ValidIssuer = _host.Server.BaseAddress.ToString().TrimEnd('/'),
            ValidAudience = clientId,
            IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
        });
        Assert.True(validation.IsValid, validation.Exception?.Message);
        Assert.Equal("n-1", validation.Claims["nonce"]);
        Assert.Equal("oauth-code", validation.Claims["tenant"]);

        // The token is limited to the granted permission scopes.
        var api = WithToken(Browser("oauth-code"), tokens.GetProperty("access_token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsJsonAsync("/v1.0/workspaces", new { name = "Not allowed" }, Ct)).StatusCode);
        var userInfo = await (await api.GetAsync("/connect/userinfo", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("oauth-code", userInfo.GetProperty("tenant").GetString());

        // A code is single-use; the refresh token stays limited and bound to its client.
        using var replay = await TokenAsync(Browser("oauth-code"), Exchange(verifier));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        using var refreshed = await TokenAsync(Browser("oauth-code"), new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        });
        Assert.Equal("workspace.read", (await refreshed.JsonAsync(HttpStatusCode.OK)).GetProperty("scope").GetString());
        using var otherClient = await TokenAsync(Browser("oauth-code"), new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "paperdotnet",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        });
        Assert.Equal(HttpStatusCode.BadRequest, otherClient.StatusCode);

        // Signing out ends the session; prompt=none then reports it to the client.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/v1.0/auth/logout", null, Ct)).StatusCode);
        var silent = await browser.GetAsync(authorize + "&prompt=none", Ct);
        Assert.Contains("error=login_required", silent.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_credentials_act_as_the_service_account()
    {
        var admin = await _host.CreateTenantAsync("oauth-client");
        var created = await CreateApplicationAsync(admin, new
        {
            displayName = "Importer",
            clientType = "confidential",
            grantTypes = new[] { "client_credentials" },
            scopes = new[] { "workspace.read", "workspace.create" },
        });
        var application = created.GetProperty("application");
        var clientId = application.GetProperty("clientId").GetString()!;
        var secret = created.GetProperty("clientSecret").GetString()!;
        var serviceUserId = application.GetProperty("serviceUserId").GetGuid();

        async Task<HttpClient> ConnectAsync(bool basic = false)
        {
            var client = Browser("oauth-client");
            var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "workspace.read workspace.create" };
            if (basic)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
            }
            else
            {
                form["client_id"] = clientId;
                form["client_secret"] = secret;
            }

            using var response = await TokenAsync(client, form);
            var token = (await response.JsonAsync(HttpStatusCode.OK)).GetProperty("access_token").GetString()!;
            return WithToken(Browser("oauth-client"), token);
        }

        // No roles yet: authenticated, but no permissions.
        Assert.Equal(HttpStatusCode.Forbidden, (await (await ConnectAsync()).GetAsync("/v1.0/workspaces", Ct)).StatusCode);

        var roles = (await (await admin.GetAsync("/v1.0/roles", Ct)).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
            .ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("id").GetString()!);
        using (var assigned = await admin.PostAsJsonAsync($"/v1.0/roles/{roles["Administrator"]}/assignments", new { principalId = serviceUserId, principalType = "user" }, Ct))
        {
            Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync(Ct));
        }

        // Administrator role, but the token is still limited to its two scopes.
        var service = await ConnectAsync(basic: true);
        Assert.Equal(HttpStatusCode.Created, (await service.PostAsJsonAsync("/v1.0/workspaces", new { name = "Imported" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync("/v1.0/users", Ct)).StatusCode);

        // The service account cannot sign in interactively.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Browser("oauth-client").PostAsJsonAsync("/v1.0/auth/login", new { userName = clientId, password = secret }, Ct)).StatusCode);

        // Secret rotation invalidates the old secret and its tokens; deleting the app disables the account.
        var id = application.GetProperty("id").GetString();
        var rotated = await (await admin.PostAsync($"/v1.0/applications/{id}/secret", null, Ct)).JsonAsync(HttpStatusCode.OK);
        using var old = await TokenAsync(Browser("oauth-client"), new() { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret });
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await service.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        secret = rotated.GetProperty("clientSecret").GetString()!;
        await ConnectAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/applications/{id}", Ct)).StatusCode);
        using var deleted = await TokenAsync(Browser("oauth-client"), new() { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret });
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
    }

    [Fact]
    public async Task Applications_are_validated_and_isolated_per_tenant()
    {
        var adminA = await _host.CreateTenantAsync("oauth-apps-a");
        var adminB = await _host.CreateTenantAsync("oauth-apps-b");

        using (var invalid = await adminA.PostAsJsonAsync("/v1.0/applications",
            new { displayName = "Bad", clientType = "public", grantTypes = new[] { "client_credentials", "password" } }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        var created = await CreateApplicationAsync(adminA, new
        {
            displayName = "Sync",
            clientType = "confidential",
            grantTypes = new[] { "client_credentials" },
            scopes = new[] { "api" },
        });
        var clientId = created.GetProperty("application").GetProperty("clientId").GetString()!;
        var secret = created.GetProperty("clientSecret").GetString()!;
        var id = created.GetProperty("application").GetProperty("id").GetString();

        var listB = await (await adminB.GetAsync("/v1.0/applications", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(listB.EnumerateArray(), a => a.GetProperty("clientId").GetString() == clientId);
        Assert.Contains(listB.EnumerateArray(), a => a.GetProperty("isFirstParty").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/v1.0/applications/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/v1.0/applications/{id}", Ct)).StatusCode);
        using var foreign = await TokenAsync(Browser("oauth-apps-b"), new() { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        // Only the first-party client may use the password grant.
        using var password = await TokenAsync(Browser("oauth-apps-a"), new()
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["username"] = "admin",
            ["password"] = TestHost.AdminPassword,
        });
        Assert.Equal("unauthorized_client", (await password.JsonAsync(HttpStatusCode.BadRequest)).GetProperty("error").GetString());

        // Members cannot register clients.
        using (var user = await adminA.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct))
        {
            await user.JsonAsync(HttpStatusCode.Created);
        }

        var member = await _host.SignInAsync("member", "member-password-1", "oauth-apps-a");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/v1.0/applications", Ct)).StatusCode);
    }
}
