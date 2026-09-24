using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

internal static class ApiClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A client for <paramref name="tenant"/> (via the X-Tenant header), signed in when credentials are given.</summary>
    public static async Task<HttpClient> CreateAsync(
        PaperDotNetApiFactory factory,
        string tenant = "default",
        string? userName = PaperDotNetApiFactory.AdminUserName,
        string password = PaperDotNetApiFactory.AdminPassword)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        if (userName is not null)
        {
            var token = await GetTokenAsync(client, userName, password);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public static async Task<string> GetTokenAsync(HttpClient client, string userName, string password)
    {
        var response = await RequestTokenAsync(client, userName, password);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>Password grant of the first-party client (<c>/connect/token</c>).</summary>
    public static Task<HttpResponseMessage> RequestTokenAsync(HttpClient client, string userName, string password, string scope = "api offline_access") =>
        client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "paperdotnet",
            ["username"] = userName,
            ["password"] = password,
            ["scope"] = scope,
        }));

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    public static async Task<Guid> CreateWorkspaceAsync(this HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/v1.0/workspaces", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }
}
