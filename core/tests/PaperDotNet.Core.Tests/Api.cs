using System.Net.Http.Headers;
using System.Text.Json;

namespace PaperDotNet.Core.Tests;

internal static class Api
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response, System.Net.HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonElement.Parse(text);
    }

    public static async Task<JsonElement> CreateListAsync(HttpClient client, string name, object? fields = null)
    {
        using var response = await client.PostAsJsonAsync("/v1.0/lists", new { name, fields });
        return await response.JsonAsync(System.Net.HttpStatusCode.Created);
    }

    public static async Task<JsonElement> CreateItemAsync(HttpClient client, string listId, object fields)
    {
        using var response = await client.PostAsJsonAsync($"/v1.0/lists/{listId}/items", new { fields });
        return await response.JsonAsync(System.Net.HttpStatusCode.Created);
    }

    public static HttpRequestMessage Patch(string uri, object body, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, uri) { Content = JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        }

        return request;
    }

    public static string Id(this JsonElement element) => element.GetProperty("id").GetString()!;

    public static string ETag(this JsonElement element) => element.GetProperty("@odata.etag").GetString()!;
}
