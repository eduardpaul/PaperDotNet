using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

internal static class Api
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonElement.Parse(text);
    }

    public static async Task<string> CreateWorkspaceAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/v1.0/workspaces", new { name });
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    public static async Task<string> CreateContentTypeAsync(HttpClient client, string name, object fields)
    {
        using var response = await client.PostAsJsonAsync("/v1.0/contentTypes", new { name, fields });
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    /// <summary>A list in the workspace, with a content type of its own when <paramref name="fields"/> are given.</summary>
    public static async Task<JsonElement> CreateListAsync(HttpClient client, string workspaceId, string name, object? fields = null)
    {
        var contentTypeIds = fields is null ? null : new[] { await CreateContentTypeAsync(client, $"{name} item", fields) };
        using var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{workspaceId}/lists", new { name, contentTypeIds });
        return await response.JsonAsync(HttpStatusCode.Created);
    }

    public static string Items(string workspaceId, string listId) => $"/v1.0/workspaces/{workspaceId}/lists/{listId}/items";

    public static async Task<JsonElement> CreateItemAsync(HttpClient client, string workspaceId, string listId, object fields)
    {
        using var response = await client.PostAsJsonAsync(Items(workspaceId, listId), new { fields });
        return await response.JsonAsync(HttpStatusCode.Created);
    }

    public static HttpRequestMessage Patch(string uri, object body, string? etag) => WithETag(HttpMethod.Patch, uri, body, etag);

    public static HttpRequestMessage WithETag(HttpMethod method, string uri, object? body, string? etag)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        }

        return request;
    }

    public static string Id(this JsonElement element) => element.GetProperty("id").GetString()!;

    public static string ETag(this JsonElement element) => element.GetProperty("@odata.etag").GetString()!;
}
