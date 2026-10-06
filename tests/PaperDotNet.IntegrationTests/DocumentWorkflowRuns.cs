using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Waiting for the workflows a document's upload starts (ADR-0038: reading the text, thumbnails, pages, OCR).</summary>
internal static class DocumentWorkflowRuns
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Waits until the item has at least <paramref name="count"/> workflow runs and all of them ended; returns them.</summary>
    public static async Task<List<JsonElement>> WaitAsync(HttpClient client, Guid workspaceId, Guid itemId, int count)
    {
        List<JsonElement> runs = [];
        try
        {
            await Eventually.WaitForAsync<bool>(async () =>
            {
                var body = await (await client.GetAsync($"/v1.0/workspaces/{workspaceId}/workflows/runs?itemId={itemId}", Ct)).ReadJsonAsync();
                runs = body.GetProperty("value").EnumerateArray().Where(r => new[] { "Read the text (", "Make thumbnails (", "Render pages (", "Recognize text (" }
                    .Any(name => r.GetProperty("workflow").GetString()?.StartsWith(name, StringComparison.Ordinal) == true)).ToList();
                return runs.Count >= count && runs.All(r => r.GetProperty("status").GetString() is "completed" or "failed" or "cancelled") ? true : null;
            }, TimeSpan.FromSeconds(90));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Expected {count} ended runs: {string.Join(", ", runs.Select(r => $"{r.GetProperty("workflow")}: {r.GetProperty("status")} {(r.TryGetProperty("error", out var error) ? error : default)}"))}");
        }

        return runs;
    }

    /// <summary>As <see cref="WaitAsync(HttpClient, Guid, Guid, int)"/> for an item's URL (<c>/v1.0/workspaces/{ws}/lists/{list}/items/{item}</c>).</summary>
    public static Task<List<JsonElement>> WaitAsync(HttpClient client, string itemUrl, int count)
    {
        var parts = itemUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return WaitAsync(client, Guid.Parse(parts[2]), Guid.Parse(parts[6]), count);
    }
}
