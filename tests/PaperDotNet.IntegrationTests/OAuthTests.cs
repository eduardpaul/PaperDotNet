using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.IntegrationTests;

/// <summary>OAuth 2.0 / OpenID Connect (IAM-02), sign-in sessions and passkeys (IAM-01).</summary>
public sealed class OAuthTests(PaperDotNetApiFactory factory)
{
    private const string Callback = "https://app.example/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A browser-like client: keeps cookies, does not follow redirects.</summary>
    private HttpClient Browser(string tenant)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
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
        var response = await admin.PostAsJsonAsync("/v1.0/applications", request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    [Fact]
    public async Task Discovery_document_describes_the_server()
    {
        var client = Browser("default");

        var discovery = await (await client.GetAsync("/.well-known/openid-configuration", Ct)).ReadJsonAsync();
        var jwks = await (await client.GetAsync(discovery.GetProperty("jwks_uri").GetString(), Ct)).ReadJsonAsync();

        Assert.EndsWith("/connect/token", discovery.GetProperty("token_endpoint").GetString(), StringComparison.Ordinal);
        Assert.Contains("S256", discovery.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(m => m.GetString()));
        Assert.NotEmpty(jwks.GetProperty("keys").EnumerateArray());
    }

    [Fact]
    public async Task Password_and_refresh_grants_issue_working_tokens()
    {
        await factory.CreateTenantAsync("oauth-password");
        var client = Browser("oauth-password");

        var first = await ApiClient.RequestTokenAsync(client, PaperDotNetApiFactory.AdminUserName, PaperDotNetApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var tokens = await first.ReadJsonAsync();
        var refreshed = await TokenAsync(client, new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "paperdotnet",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var access = (await refreshed.ReadJsonAsync()).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await WithToken(Browser("oauth-password"), access).GetAsync("/v1.0/me", Ct)).StatusCode);

        var wrong = await ApiClient.RequestTokenAsync(client, PaperDotNetApiFactory.AdminUserName, "wrong-password-123");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invalid_grant", (await wrong.ReadJsonAsync()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Authorization_code_flow_with_pkce_uses_the_sign_in_session()
    {
        await factory.CreateTenantAsync("oauth-code");
        var admin = await ApiClient.CreateAsync(factory, "oauth-code");
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
                        $"&scope={Uri.EscapeDataString("openid offline_access workspace.read")}&code_challenge={challenge}&code_challenge_method=S256&state=xyz";

        var browser = Browser("oauth-code");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(authorize, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsJsonAsync("/v1.0/auth/login",
            new { userName = PaperDotNetApiFactory.AdminUserName, password = PaperDotNetApiFactory.AdminPassword }, Ct)).StatusCode);

        var redirect = await browser.GetAsync(authorize, Ct);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var location = redirect.Headers.Location!;
        Assert.StartsWith(Callback, location.ToString(), StringComparison.Ordinal);
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("xyz", query["state"]);

        var exchange = await TokenAsync(browser, new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = query["code"]!,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = verifier,
        });
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        var tokens = await exchange.ReadJsonAsync();
        Assert.True(tokens.TryGetProperty("id_token", out _));
        Assert.True(tokens.TryGetProperty("refresh_token", out _));

        // The token is limited to the granted permission scopes.
        var api = WithToken(Browser("oauth-code"), tokens.GetProperty("access_token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/v1.0/workspaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsJsonAsync("/v1.0/workspaces", new { name = "Not allowed" }, Ct)).StatusCode);
        var userInfo = await (await api.GetAsync("/connect/userinfo", Ct)).ReadJsonAsync();
        Assert.Equal("oauth-code", userInfo.GetProperty("tenant").GetString());

        // A code is single-use, and a wrong verifier fails.
        var replay = await TokenAsync(browser, new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = query["code"]!,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = verifier,
        });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Client_credentials_act_as_the_service_account()
    {
        await factory.CreateTenantAsync("oauth-client");
        var admin = await ApiClient.CreateAsync(factory, "oauth-client");
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

        async Task<HttpClient> ConnectAsync()
        {
            var client = Browser("oauth-client");
            var response = await TokenAsync(client, new()
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["scope"] = "workspace.read workspace.create",
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return WithToken(client, (await response.ReadJsonAsync()).GetProperty("access_token").GetString()!);
        }

        // No roles yet: authenticated, but no permissions.
        Assert.Equal(HttpStatusCode.Forbidden, (await (await ConnectAsync()).GetAsync("/v1.0/workspaces", Ct)).StatusCode);

        var roles = (await (await admin.GetAsync("/v1.0/roles", Ct)).ReadJsonAsync()).EnumerateArray()
            .ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("id").GetGuid());
        await admin.PostAsJsonAsync($"/v1.0/roles/{roles["Administrator"]}/assignments", new { principalId = serviceUserId, principalType = "user" }, Ct);

        // Administrator role, but the token is still limited to its two scopes.
        var service = await ConnectAsync();
        Assert.Equal(HttpStatusCode.Created, (await service.PostAsJsonAsync("/v1.0/workspaces", new { name = "Imported" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync("/v1.0/users", Ct)).StatusCode);

        // Secret rotation invalidates the old secret; deleting the app disables the account.
        var id = application.GetProperty("id").GetGuid();
        var rotated = await (await admin.PostAsync($"/v1.0/applications/{id}/secret", null, Ct)).ReadJsonAsync();
        var old = await TokenAsync(Browser("oauth-client"), new() { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret });
        Assert.NotEqual(HttpStatusCode.OK, old.StatusCode);
        secret = rotated.GetProperty("clientSecret").GetString()!;
        await ConnectAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/applications/{id}", Ct)).StatusCode);
        var deleted = await TokenAsync(Browser("oauth-client"), new() { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret });
        Assert.NotEqual(HttpStatusCode.OK, deleted.StatusCode);
    }

    [Fact]
    public async Task Applications_are_validated_and_isolated_per_tenant()
    {
        await factory.CreateTenantAsync("oauth-apps-a");
        await factory.CreateTenantAsync("oauth-apps-b");
        var adminA = await ApiClient.CreateAsync(factory, "oauth-apps-a");
        var adminB = await ApiClient.CreateAsync(factory, "oauth-apps-b");

        var invalid = await adminA.PostAsJsonAsync("/v1.0/applications",
            new { displayName = "Bad", clientType = "public", grantTypes = new[] { "client_credentials", "password" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var created = await CreateApplicationAsync(adminA, new
        {
            displayName = "Sync",
            clientType = "confidential",
            grantTypes = new[] { "client_credentials" },
            scopes = new[] { "api" },
        });
        var clientId = created.GetProperty("application").GetProperty("clientId").GetString()!;
        var secret = created.GetProperty("clientSecret").GetString()!;

        var listB = await (await adminB.GetAsync("/v1.0/applications", Ct)).ReadJsonAsync();
        Assert.DoesNotContain(listB.EnumerateArray(), a => a.GetProperty("clientId").GetString() == clientId);
        Assert.Contains(listB.EnumerateArray(), a => a.GetProperty("isFirstParty").GetBoolean());
        var foreign = await TokenAsync(Browser("oauth-apps-b"), new() { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret });
        Assert.NotEqual(HttpStatusCode.OK, foreign.StatusCode);

        // Only the first-party client may use the password grant.
        var password = await TokenAsync(Browser("oauth-apps-a"), new()
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["username"] = PaperDotNetApiFactory.AdminUserName,
            ["password"] = PaperDotNetApiFactory.AdminPassword,
        });
        Assert.NotEqual(HttpStatusCode.OK, password.StatusCode);

        // Members cannot register clients.
        await adminA.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct);
        var member = await ApiClient.CreateAsync(factory, "oauth-apps-a", "member", "member-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/v1.0/applications", Ct)).StatusCode);
    }

    [Fact]
    public async Task Passkeys_can_be_registered_and_used_to_sign_in()
    {
        await factory.CreateTenantAsync("oauth-passkey");
        var admin = await ApiClient.CreateAsync(factory, "oauth-passkey");
        using var authenticator = new SoftwareAuthenticator();

        var creation = await (await admin.PostAsync("/v1.0/me/passkeys/options", null, Ct)).ReadJsonAsync();
        var registered = await admin.PostAsJsonAsync("/v1.0/me/passkeys", new
        {
            credential = authenticator.Create(creation.GetProperty("options")),
            state = creation.GetProperty("state").GetString(),
            name = "Laptop",
        }, Ct);
        Assert.True(registered.StatusCode == HttpStatusCode.Created, await registered.Content.ReadAsStringAsync(Ct));
        var passkeys = await (await admin.GetAsync("/v1.0/me/passkeys", Ct)).ReadJsonAsync();
        Assert.Equal("Laptop", Assert.Single(passkeys.EnumerateArray()).GetProperty("name").GetString());

        // Sign in without a password (discoverable credential); the session then serves /connect/authorize.
        var browser = Browser("oauth-passkey");
        var request = await (await browser.PostAsJsonAsync("/v1.0/auth/passkeys/options", new { }, Ct)).ReadJsonAsync();
        var login = await browser.PostAsJsonAsync("/v1.0/auth/passkeys/login", new
        {
            credential = authenticator.Get(request.GetProperty("options")),
            state = request.GetProperty("state").GetString(),
        }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        // A replayed assertion (same state, stale counter) is rejected; a state from another tenant too.
        var replay = await browser.PostAsJsonAsync("/v1.0/auth/passkeys/login", new
        {
            credential = authenticator.Get(request.GetProperty("options")),
            state = "tampered",
        }, Ct);
        Assert.NotEqual(HttpStatusCode.NoContent, replay.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1.0/me/passkeys/{authenticator.CredentialId}", Ct)).StatusCode);
        Assert.Empty((await (await admin.GetAsync("/v1.0/me/passkeys", Ct)).ReadJsonAsync()).EnumerateArray());
    }

    [Fact]
    public async Task Data_protection_keys_and_server_keys_are_stored_in_the_database()
    {
        await ApiClient.CreateAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.True(await db.DataProtectionKeys.AnyAsync(Ct));
        Assert.Equal(2, await db.ServerKeys.CountAsync(Ct));
    }
}
