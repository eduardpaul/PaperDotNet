using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Markdown notes (LST-18): #tags, [[wiki links]], backlinks and renames.</summary>
public sealed class NoteTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<JsonElement> WaitAsync(string url, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var body = await (await _client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);
            if (condition(body))
            {
                return body;
            }

            Assert.True(DateTime.UtcNow < deadline, $"{url}: {body}");
            await Task.Delay(100, Ct);
        }
    }

    private static List<JsonElement> Values(JsonElement body) => [.. body.GetProperty("value").EnumerateArray()];

    /// <summary>Whether the link points to a note (null properties are left out of responses).</summary>
    private static bool Resolved(JsonElement link) => link.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.Object;

    [Fact]
    public async Task Notes_have_tags_links_and_backlinks_that_follow_renames()
    {
        var ws = await Api.CreateWorkspaceAsync(_client, "Wiki");
        using var created = await _client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Notes", templateKey = "notes" }, Ct);
        var list = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        string Item(string id) => $"{Api.Items(ws, list)}/{id}";

        var created1 = await Api.CreateItemAsync(_client, ws, list, new
        {
            title = "Project Alpha",
            body = "# Plan\nWork with #planning and #team/core.\nSee [[Meeting Notes#Actions|the meeting]] and [[Missing page]].\n`[[code]]` `#code` and ```\n[[Not a link]]\n```",
        });
        var alpha = created1.Id();

        // #tags become keywords (code is ignored).
        var tags = created1.GetProperty("fields").GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(2, tags.Count);
        using var planning = await _client.GetAsync("/v1.0/termStore/keywords?search=planning", Ct);
        Assert.Contains((await planning.JsonAsync(HttpStatusCode.OK))[0].Id(), tags);

        // Links wait for their notes, and resolve when one gets the title.
        var links = await WaitAsync($"{Item(alpha)}/noteLinks", b => Values(b).Count == 2);
        Assert.Equal(["Meeting Notes", "Missing page"], Values(links).Select(l => l.GetProperty("target").GetString()));
        Assert.All(Values(links), l => Assert.False(Resolved(l)));
        var meeting = (await Api.CreateItemAsync(_client, ws, list, new { title = "Meeting notes", body = "Back to [[project alpha]]." })).Id();
        links = await WaitAsync($"{Item(alpha)}/noteLinks", b => Resolved(Values(b)[0]));
        var first = Values(links)[0];
        Assert.Equal(meeting, first.GetProperty("note").GetProperty("itemId").GetString());
        Assert.Equal("Actions", first.GetProperty("heading").GetString());
        Assert.Equal("the meeting", first.GetProperty("alias").GetString());
        await WaitAsync($"{Item(meeting)}/backlinks", b => Values(b).Any(n => n.GetProperty("itemId").GetString() == alpha));
        await WaitAsync($"{Item(alpha)}/backlinks", b => Values(b).Any(n => n.GetProperty("itemId").GetString() == meeting));

        // Renaming a note rewrites the links to it, keeping heading and alias.
        var etag = (await _client.GetAsync(Item(meeting), Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Api.Patch(Item(meeting), new { fields = new { title = "Weekly meeting" } }, etag), Ct)).StatusCode);
        var rewritten = await WaitAsync(Item(alpha), i => i.GetProperty("fields").GetProperty("body").GetString()!.Contains("[[Weekly meeting#Actions|the meeting]]", StringComparison.Ordinal));
        Assert.Contains("[[Not a link]]", rewritten.GetProperty("fields").GetProperty("body").GetString(), StringComparison.Ordinal);
        await WaitAsync($"{Item(alpha)}/noteLinks", b => Resolved(Values(b)[0]) && Values(b)[0].GetProperty("target").GetString() == "Weekly meeting");

        // Deleting a note leaves its links waiting again.
        etag = (await _client.GetAsync(Item(meeting), Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(Api.WithETag(HttpMethod.Delete, Item(meeting), null, etag), Ct)).StatusCode);
        await WaitAsync($"{Item(alpha)}/noteLinks", b => !Values(b).Any(Resolved));
        await WaitAsync($"{Item(alpha)}/backlinks", b => Values(b).Count == 0);

        // Other tenants see nothing.
        var other = await _host.CreateTenantAsync("other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Item(alpha)}/noteLinks", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Item(alpha)}/backlinks", Ct)).StatusCode);
    }
}
