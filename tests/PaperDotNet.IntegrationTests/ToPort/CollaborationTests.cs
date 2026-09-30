using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Comments, @mentions and the activity timeline of items (LST-17).</summary>
public sealed class CollaborationTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(HttpClient Admin, string Tenant, Guid Workspace, Guid List, Guid Item, Dictionary<string, Guid> Users)
    {
        public string ItemUrl => $"/v1.0/workspaces/{Workspace}/lists/{List}/items/{Item}";
    }

    /// <summary>Workspace with member alice, visitor vic and outsider otto (no access); one item "Contract".</summary>
    private async Task<Setup> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var admin = await ApiClient.CreateAsync(factory, tenant);
        var ws = await admin.CreateWorkspaceAsync("Legal");
        var contentType = await admin.CreateContentTypeAsync("Paper", [new { name = "note", type = "text" }]);
        var list = await admin.CreateListAsync(ws, "Papers", contentType);
        var users = new Dictionary<string, Guid>();
        foreach (var (name, role) in new[] { ("alice", "member"), ("vic", "visitor"), ("otto", null) })
        {
            var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
            users[name] = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
            if (role is not null)
            {
                await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = users[name], role }, Ct);
            }
        }

        var item = (await admin.CreateItemAsync(ws, list, new { fields = new { title = "Contract" } })).GetProperty("id").GetGuid();
        return new Setup(admin, tenant, ws, list, item, users);
    }

    private Task<HttpClient> AsAsync(Setup setup, string user) => ApiClient.CreateAsync(factory, setup.Tenant, user, $"{user}-password-1");

    private static async Task<JsonElement> WaitAsync(HttpClient client, string url, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var body = await (await client.GetAsync(url, Ct)).ReadJsonAsync();
            if (condition(body))
            {
                return body;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{url}: {body}");
            }

            await Task.Delay(200, Ct);
        }
    }

    private static List<string> Kinds(JsonElement page) =>
        page.GetProperty("value").EnumerateArray().Select(a => a.GetProperty("kind").GetString()!).ToList();

    [Fact]
    public async Task Members_comment_reply_and_mention_people_who_can_read_the_item()
    {
        var setup = await SetupAsync("comments-basic");
        var alice = await AsAsync(setup, "alice");

        var created = await alice.PostAsJsonAsync($"{setup.ItemUrl}/comments",
            new { text = "Please check clause zebracorn, @vic @otto", mentions = new[] { setup.Users["vic"], setup.Users["otto"] } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var comment = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
        var reply = await setup.Admin.PostAsJsonAsync($"{setup.ItemUrl}/comments", new { text = "Done.", parentId = comment }, Ct);
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        var nested = await setup.Admin.PostAsJsonAsync($"{setup.ItemUrl}/comments",
            new { text = "Nested", parentId = (await reply.ReadJsonAsync()).GetProperty("id").GetGuid() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nested.StatusCode);

        var vic = await AsAsync(setup, "vic");
        var comments = (await (await vic.GetAsync($"{setup.ItemUrl}/comments", Ct)).ReadJsonAsync()).GetProperty("value");
        Assert.Equal(["Please check clause zebracorn, @vic @otto", "Done."], comments.EnumerateArray().Select(c => c.GetProperty("text").GetString()));
        Assert.Equal(comment, comments[1].GetProperty("parentId").GetGuid());

        // Only vic can read the item; otto is not notified.
        var inbox = (await (await vic.GetAsync("/v1.0/me/notifications", Ct)).ReadJsonAsync()).GetProperty("value");
        Assert.Contains(inbox.EnumerateArray(), n => n.GetProperty("type").GetString() == "mention");
        var otto = await AsAsync(setup, "otto");
        Assert.DoesNotContain((await (await otto.GetAsync("/v1.0/me/notifications", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray(),
            n => n.GetProperty("type").GetString() == "mention");
        Assert.Equal(HttpStatusCode.NotFound, (await otto.GetAsync($"{setup.ItemUrl}/comments", Ct)).StatusCode);

        // Comments are part of the item's search document.
        await WaitAsync(alice, "/v1.0/search?q=zebracorn", r => r.GetProperty("value").GetArrayLength() == 1);

        // The timeline shows the item's changes and the comments, newest first.
        var etag = (await alice.GetAsync(setup.ItemUrl, Ct)).Headers.ETag!.Tag;
        await alice.SendWithEtagAsync(HttpMethod.Patch, setup.ItemUrl, etag, new { fields = new { note = "signed" } });
        var activity = await WaitAsync(vic, $"{setup.ItemUrl}/activity", a => Kinds(a).Contains("updated"));
        Assert.Equal(["updated", "commented", "commented", "created"], Kinds(activity));
        var updated = activity.GetProperty("value")[0];
        Assert.Equal(setup.Users["alice"], updated.GetProperty("actorId").GetGuid());
        Assert.Contains("note", updated.GetProperty("changedFields").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task Only_authors_edit_and_managers_or_authors_delete_comments()
    {
        var setup = await SetupAsync("comments-rules");
        var alice = await AsAsync(setup, "alice");
        var vic = await AsAsync(setup, "vic");
        Assert.Equal(HttpStatusCode.Forbidden, (await vic.PostAsJsonAsync($"{setup.ItemUrl}/comments", new { text = "Visitors read only" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"{setup.ItemUrl}/comments", new { text = " " }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"{setup.ItemUrl}/comments", new { text = "Hi", mentions = new[] { Guid.NewGuid() } }, Ct)).StatusCode);

        var created = await alice.PostAsJsonAsync($"{setup.ItemUrl}/comments", new { text = "First draft" }, Ct);
        var url = $"{setup.ItemUrl}/comments/{(await created.ReadJsonAsync()).GetProperty("id").GetGuid()}";
        var etag = created.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.Forbidden, (await setup.Admin.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { text = "Not mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await alice.PatchAsJsonAsync(url, new { text = "No etag" }, Ct)).StatusCode);
        var edited = await alice.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { text = "Final" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await alice.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { text = "Stale" })).StatusCode);
        Assert.Equal("Final", (await (await vic.GetAsync(url, Ct)).ReadJsonAsync()).GetProperty("text").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await vic.DeleteAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Admin.DeleteAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task Comments_and_activity_of_another_tenant_are_invisible()
    {
        var setup = await SetupAsync("comments-isolation-a");
        await setup.Admin.PostAsJsonAsync($"{setup.ItemUrl}/comments", new { text = "Secret" }, Ct);
        await factory.CreateTenantAsync("comments-isolation-b");
        var other = await ApiClient.CreateAsync(factory, "comments-isolation-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{setup.ItemUrl}/comments", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{setup.ItemUrl}/activity", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{setup.ItemUrl}/comments", new { text = "Intrusion" }, Ct)).StatusCode);
    }
}
