using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Comments, @mentions and the activity timeline of items (LST-17).</summary>
public sealed class CollaborationTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private readonly Dictionary<string, Guid> _users = [];
    private readonly Dictionary<string, HttpClient> _clients = [];
    private HttpClient _admin = null!;
    private string _workspace = "";
    private string _list = "";
    private string _item = "";

    private string ItemUrl => $"{Api.Items(_workspace, _list)}/{_item}";

    /// <summary>Workspace with member alice, visitor vic and outsider otto (no access); one item "Contract".</summary>
    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Legal");
        _list = (await Api.CreateListAsync(_admin, _workspace, "Papers", new[] { new { name = "note", type = "text" } })).Id();
        foreach (var (name, role) in new[] { ("alice", "member"), ("vic", "visitor"), ("otto", null) })
        {
            using var created = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
            _users[name] = Guid.Parse((await created.JsonAsync(HttpStatusCode.Created)).Id());
            if (role is not null)
            {
                await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/members", new { userId = _users[name], role }, Ct);
            }

            _clients[name] = await _host.SignInAsync(name, $"{name}-password-1");
        }

        _item = (await Api.CreateItemAsync(_admin, _workspace, _list, new { title = "Contract" })).Id();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<JsonElement> WaitAsync(HttpClient client, string url, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var body = await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);
            if (condition(body))
            {
                return body;
            }

            Assert.True(DateTime.UtcNow < deadline, $"{url}: {body}");
            await Task.Delay(200, Ct);
        }
    }

    private static List<string> Kinds(JsonElement page) => [.. page.GetProperty("value").EnumerateArray().Select(a => a.GetProperty("kind").GetString()!)];

    [Fact]
    public async Task Members_comment_reply_and_mention_people_who_can_read_the_item()
    {
        var alice = _clients["alice"];
        using var created = await alice.PostAsJsonAsync($"{ItemUrl}/comments",
            new { text = "Please check clause 4, @vic @otto", mentions = new[] { _users["vic"], _users["otto"] } }, Ct);
        var comment = (await created.JsonAsync(HttpStatusCode.Created)).Id();
        using var reply = await _admin.PostAsJsonAsync($"{ItemUrl}/comments", new { text = "Done.", parentId = comment }, Ct);
        var replyId = (await reply.JsonAsync(HttpStatusCode.Created)).Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync($"{ItemUrl}/comments", new { text = "Nested", parentId = replyId }, Ct)).StatusCode);

        var vic = _clients["vic"];
        var comments = (await (await vic.GetAsync($"{ItemUrl}/comments", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value");
        Assert.Equal(["Please check clause 4, @vic @otto", "Done."], comments.EnumerateArray().Select(c => c.GetProperty("text").GetString()));
        Assert.Equal(comment, comments[1].GetProperty("parentId").GetString());
        Assert.Equal([_users["vic"], _users["otto"]], comments[0].GetProperty("mentions").EnumerateArray().Select(m => m.GetGuid()));

        // Only vic can read the item; otto is not notified and sees nothing.
        var inbox = (await (await vic.GetAsync("/v1.0/me/notifications", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value");
        var mention = Assert.Single(inbox.EnumerateArray(), n => n.GetProperty("type").GetString() == "mention");
        Assert.Equal(_item, mention.GetProperty("itemId").GetString());
        var otto = _clients["otto"];
        Assert.Empty((await (await otto.GetAsync("/v1.0/me/notifications", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await otto.GetAsync($"{ItemUrl}/comments", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otto.GetAsync($"{ItemUrl}/activity", Ct)).StatusCode);

        // The timeline shows the item's changes and the comments, newest first.
        var etag = (await alice.GetAsync(ItemUrl, Ct)).Headers.ETag!.Tag;
        (await alice.SendAsync(Api.Patch(ItemUrl, new { fields = new { note = "signed" } }, etag), Ct)).EnsureSuccessStatusCode();
        var activity = await WaitAsync(vic, $"{ItemUrl}/activity", a => Kinds(a).Contains("updated") && Kinds(a).Contains("created"));
        Assert.Equal(["updated", "commented", "commented", "created"], Kinds(activity));
        var updated = activity.GetProperty("value")[0];
        Assert.Equal(_users["alice"], updated.GetProperty("actorId").GetGuid());
        Assert.Contains("note", updated.GetProperty("changedFields").EnumerateArray().Select(f => f.GetString()));

        // Paging walks the timeline in the same order.
        var first = await (await vic.GetAsync($"{ItemUrl}/activity?$top=3", Ct)).JsonAsync(HttpStatusCode.OK);
        var next = await (await vic.GetAsync(first.GetProperty("@odata.nextLink").GetString(), Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(["updated", "commented", "commented"], Kinds(first));
        Assert.Equal(["created"], Kinds(next));
    }

    [Fact]
    public async Task Only_authors_edit_and_managers_or_authors_delete_comments()
    {
        var alice = _clients["alice"];
        var vic = _clients["vic"];
        Assert.Equal(HttpStatusCode.Forbidden, (await vic.PostAsJsonAsync($"{ItemUrl}/comments", new { text = "Visitors read only" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"{ItemUrl}/comments", new { text = " " }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"{ItemUrl}/comments", new { text = "Hi", mentions = new[] { Guid.NewGuid() } }, Ct)).StatusCode);

        using var created = await alice.PostAsJsonAsync($"{ItemUrl}/comments", new { text = "First draft" }, Ct);
        var url = $"{ItemUrl}/comments/{(await created.JsonAsync(HttpStatusCode.Created)).Id()}";
        var etag = created.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.SendAsync(Api.Patch(url, new { text = "Not mine" }, etag), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await alice.PatchAsJsonAsync(url, new { text = "No etag" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.SendAsync(Api.Patch(url, new { text = "Final" }, etag), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await alice.SendAsync(Api.Patch(url, new { text = "Stale" }, etag), Ct)).StatusCode);
        Assert.Equal("Final", (await (await vic.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("text").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await vic.DeleteAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task Modules_and_extensions_add_timeline_entries_once()
    {
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var tenant = (await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync("default", Ct))!.Id;
            var activity = scope.ServiceProvider.GetRequiredService<IItemActivity>();
            var entry = new ItemActivityEntry(Guid.Parse(_workspace), Guid.Parse(_list), Guid.Parse(_item), "acme.signed", "Signed by both parties", "signature:1");
            await activity.RecordAsync(new ChangeActor(tenant, _users["alice"]), entry, Ct);
            await activity.RecordAsync(new ChangeActor(tenant, _users["alice"]), entry, Ct);
        }

        var timeline = await WaitAsync(_admin, $"{ItemUrl}/activity", a => Kinds(a).Contains("created"));
        var signed = Assert.Single(timeline.GetProperty("value").EnumerateArray(), a => a.GetProperty("kind").GetString() == "acme.signed");
        Assert.Equal("Signed by both parties", signed.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Comments_and_activity_of_another_tenant_are_invisible()
    {
        await _admin.PostAsJsonAsync($"{ItemUrl}/comments", new { text = "Secret" }, Ct);
        var other = await _host.CreateTenantAsync("other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{ItemUrl}/comments", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{ItemUrl}/activity", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{ItemUrl}/comments", new { text = "Intrusion" }, Ct)).StatusCode);
    }
}
