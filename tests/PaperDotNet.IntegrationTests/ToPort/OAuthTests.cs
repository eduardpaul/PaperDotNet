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

/// <summary>Passkeys (IAM-01): still to port (T17d2); the OAuth cases are in OAuthTests.cs.</summary>
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
}
