using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Helpers for the lists engine endpoints.</summary>
internal static class ListsApi
{
    public static async Task<Guid> CreateContentTypeAsync(this HttpClient client, string name, object[] fields)
    {
        var response = await client.PostAsJsonAsync("/v1.0/contentTypes", new { name, fields });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> CreateListAsync(this HttpClient client, Guid workspaceId, string name, params Guid[] contentTypeIds)
    {
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{workspaceId}/lists", new { name, contentTypeIds });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> PostItemAsync(this HttpClient client, Guid workspaceId, Guid listId, object body) =>
        client.PostAsJsonAsync($"/v1.0/workspaces/{workspaceId}/lists/{listId}/items", body);

    public static async Task<JsonElement> CreateItemAsync(this HttpClient client, Guid workspaceId, Guid listId, object body)
    {
        var response = await client.PostItemAsync(workspaceId, listId, body);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return await response.ReadJsonAsync();
    }

    public static async Task<List<string>> QueryTitlesAsync(this HttpClient client, Guid workspaceId, Guid listId, string query)
    {
        var body = await (await client.GetAsync($"/v1.0/workspaces/{workspaceId}/lists/{listId}/items?{query}")).ReadJsonAsync();
        return body.GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()!).ToList();
    }

    public static async Task<HttpResponseMessage> SendWithEtagAsync(this HttpClient client, HttpMethod method, string url, string etag, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }
}
