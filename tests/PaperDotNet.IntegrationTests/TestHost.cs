using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Identity.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// A host on a fresh SQLite database with the tenant <c>default</c> and its administrator, optionally with more
/// services (e.g. a test job) and settings.
/// </summary>
public sealed class TestHost(Action<IServiceCollection>? services = null, IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<Program>
{
    public const string AdminPassword = "Admin-Pass-123";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "pdn-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:DataPath", _dataPath);
        builder.UseSetting("Bootstrap:TenantIdentifier", "default");
        builder.UseSetting("Bootstrap:AdminUserName", "admin");
        builder.UseSetting("Bootstrap:AdminPassword", AdminPassword);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        if (services is not null)
        {
            builder.ConfigureServices(services);
        }
    }

    /// <summary>A client signed in with the password grant.</summary>
    public async Task<HttpClient> SignInAsync(string userName = "admin", string password = AdminPassword, string tenant = "default")
    {
        var client = CreateClient();
        var token = await RequestTokenAsync(client, new() { ["grant_type"] = "password", ["username"] = userName, ["password"] = password, ["tenant"] = tenant });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
        return client;
    }

    public static async Task<JsonElement> RequestTokenAsync(HttpClient client, Dictionary<string, string> form)
    {
        using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return body;
    }

    /// <summary>Creates another tenant with its administrator (<c>admin</c>) and signs in there.</summary>
    public async Task<HttpClient> CreateTenantAsync(string identifier)
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TenantProvisioner>().CreateAsync(identifier, identifier, "admin", AdminPassword);
        }

        return await SignInAsync(tenant: identifier);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
