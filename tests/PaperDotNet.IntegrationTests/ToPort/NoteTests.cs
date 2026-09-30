using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Markdown notes (LST-18): #tags, [[wiki links]], backlinks and renames.</summary>
public sealed class NoteTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode}");
        return await response.ReadJsonAsync();
    }

    private static async Task<JsonElement> WaitAsync(HttpClient client, string url, Func<JsonElement, bool> condition)
    {
        JsonElement last = default;
        await Eventually.WaitForAsync<bool>(async () =>
        {
            last = await GetAsync(client, url);
            return condition(last) ? true : null;
        });
        return last;
    }

    private static List<JsonElement> Values(JsonElement body) => [.. body.GetProperty("value").EnumerateArray()];

    /// <summary>Whether the link points to a note (null properties are left out of responses).</summary>
    private static bool Resolved(JsonElement link) => link.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.Object;

    [Fact]
    public async Task Notes_have_tags_links_and_backlinks_that_follow_renames()
    {
        await factory.CreateTenantAsync("notes");
        var client = await ApiClient.CreateAsync(factory, "notes");
        var ws = await client.CreateWorkspaceAsync("Wiki");
        var list = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Notes", templateKey = "notes" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        string Item(Guid id) => $"/v1.0/workspaces/{ws}/lists/{list}/items/{id}";

        var alpha = await client.CreateItemAsync(ws, list, new
        {
            fields = new
            {
                title = "Project Alpha",
                body = "# Plan\nWork with #planning and #team/core.\nSee [[Meeting Notes#Actions|the meeting]] and [[Missing page]].\n`#code` and ```\n[[Not a link]]\n```",
            },
        });
        var alphaId = alpha.GetProperty("id").GetGuid();

        // #tags become keywords (code is ignored).
        Assert.Equal(2, alpha.GetProperty("fields").GetProperty("tags").GetArrayLength());

        // Links wait for their notes, and resolve when one gets the title.
        var links = await WaitAsync(client, $"{Item(alphaId)}/noteLinks", b => Values(b).Count == 2);
        Assert.Equal(["Meeting Notes", "Missing page"], Values(links).Select(l => l.GetProperty("target").GetString()));
        Assert.All(Values(links), l => Assert.False(Resolved(l)));
        var meeting = (await client.CreateItemAsync(ws, list, new { fields = new { title = "Meeting notes", body = "Back to [[project alpha]]." } })).GetProperty("id").GetGuid();
        links = await WaitAsync(client, $"{Item(alphaId)}/noteLinks", b => Resolved(Values(b)[0]));
        var first = Values(links)[0];
        Assert.Equal(meeting, first.GetProperty("note").GetProperty("itemId").GetGuid());
        Assert.Equal("Actions", first.GetProperty("heading").GetString());
        Assert.Equal("the meeting", first.GetProperty("alias").GetString());
        await WaitAsync(client, $"{Item(meeting)}/backlinks", b => Values(b).Any(n => n.GetProperty("itemId").GetGuid() == alphaId));
        await WaitAsync(client, $"{Item(alphaId)}/backlinks", b => Values(b).Any(n => n.GetProperty("itemId").GetGuid() == meeting));

        // Renaming a note rewrites the links to it, keeping heading and alias.
        var etag = (await client.GetAsync(Item(meeting), Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await client.SendWithEtagAsync(HttpMethod.Patch, Item(meeting), etag, new { fields = new { title = "Weekly meeting" } })).StatusCode);
        var rewritten = await WaitAsync(client, Item(alphaId), i => i.GetProperty("fields").GetProperty("body").GetString()!.Contains("[[Weekly meeting#Actions|the meeting]]", StringComparison.Ordinal));
        Assert.Contains("[[Not a link]]", rewritten.GetProperty("fields").GetProperty("body").GetString(), StringComparison.Ordinal);
        await WaitAsync(client, $"{Item(alphaId)}/noteLinks", b => Resolved(Values(b)[0]) && Values(b)[0].GetProperty("target").GetString() == "Weekly meeting");

        // Deleting a note leaves its links waiting again.
        etag = (await client.GetAsync(Item(meeting), Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendWithEtagAsync(HttpMethod.Delete, Item(meeting), etag)).StatusCode);
        await WaitAsync(client, $"{Item(alphaId)}/noteLinks", b => !Values(b).Any(Resolved));
        await WaitAsync(client, $"{Item(alphaId)}/backlinks", b => Values(b).Count == 0);

        // Other tenants see nothing.
        await factory.CreateTenantAsync("notes-b");
        var other = await ApiClient.CreateAsync(factory, "notes-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Item(alphaId)}/noteLinks", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Item(alphaId)}/backlinks", Ct)).StatusCode);
    }
}
