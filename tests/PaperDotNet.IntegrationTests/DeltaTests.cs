using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Delta sync of lists (API-05).</summary>
public sealed class DeltaTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // No safety window: the test reads its own changes at once.
    private readonly TestHost _host = new(settings: new Dictionary<string, string> { ["Lists:DeltaSafetyWindow"] = "00:00:00" });
    private HttpClient _admin = null!;
    private string _workspace = "";
    private string _list = "";

    private string ListUrl => $"/v1.0/workspaces/{_workspace}/lists/{_list}";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Sync");
        _list = (await Api.CreateListAsync(_admin, _workspace, "Notes", new[] { new { name = "text", type = "text" } })).Id();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<string> CreateAsync(HttpClient client, string title, string? parentId = null, bool isFolder = false)
    {
        using var response = await client.PostAsJsonAsync($"{ListUrl}/items", new { parentId, isFolder, fields = new { title } }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    /// <summary>Follows nextLinks; returns all entries and the deltaLink.</summary>
    private static async Task<(List<JsonElement> Entries, string DeltaLink, int Pages)> SyncAsync(HttpClient client, string url)
    {
        var entries = new List<JsonElement>();
        for (var pages = 1; ; pages++)
        {
            var body = await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK);
            entries.AddRange(body.GetProperty("value").EnumerateArray());
            if (body.TryGetProperty("@odata.deltaLink", out var delta))
            {
                return (entries, delta.GetString()!, pages);
            }

            url = body.GetProperty("@odata.nextLink").GetString()!;
        }
    }

    private static async Task<HttpStatusCode> DeleteAsync(HttpClient client, string url)
    {
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        return (await client.SendAsync(Api.WithETag(HttpMethod.Delete, url, null, etag), Ct)).StatusCode;
    }

    private static string Title(JsonElement entry) => entry.GetProperty("fields").GetProperty("title").GetString()!;

    private static bool Removed(JsonElement entry) => entry.TryGetProperty("@removed", out _);

    [Fact]
    public async Task Delta_returns_all_items_first_and_then_only_changes()
    {
        var a = await CreateAsync(_admin, "A");
        var b = await CreateAsync(_admin, "B");
        await CreateAsync(_admin, "C");

        var (initial, deltaLink, pages) = await SyncAsync(_admin, $"{ListUrl}/items/delta?$top=2");
        Assert.Equal(2, pages);
        Assert.Equal(["A", "B", "C"], initial.Select(Title).Order(StringComparer.Ordinal));

        var (none, same, _) = await SyncAsync(_admin, deltaLink);
        Assert.Empty(none);

        var etag = (await _admin.GetAsync($"{ListUrl}/items/{a}", Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await _admin.SendAsync(Api.Patch($"{ListUrl}/items/{a}", new { fields = new { title = "A2" } }, etag), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(_admin, $"{ListUrl}/items/{b}"));
        var d = await CreateAsync(_admin, "D");

        var (changes, next, _) = await SyncAsync(_admin, same);
        Assert.Equal(3, changes.Count);
        Assert.Equal("A2", Title(changes.Single(e => e.Id() == a)));
        Assert.True(Removed(changes.Single(e => e.Id() == b)));
        Assert.Equal("D", Title(changes.Single(e => e.Id() == d)));

        Assert.Empty((await SyncAsync(_admin, next)).Entries);

        // Restoring from the recycle bin brings the item back.
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"{ListUrl}/recycleBin/{b}/restore", null, Ct)).StatusCode);
        var restored = (await SyncAsync(_admin, next)).Entries;
        Assert.Equal("B", Title(Assert.Single(restored)));
    }

    [Fact]
    public async Task Delta_follows_item_permissions_without_a_resync()
    {
        string aliceId;
        using (var alice = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = "alice", password = "alice-password-1" }, Ct))
        {
            aliceId = (await alice.JsonAsync(HttpStatusCode.Created)).Id();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/members", new { userId = aliceId, role = "member" }, Ct)).StatusCode);
        var folder = await CreateAsync(_admin, "Private", isFolder: true);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"{ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        var shared = await CreateAsync(_admin, "Shared", isFolder: true);
        var plan = await CreateAsync(_admin, "Plan", shared);
        await CreateAsync(_admin, "Public");
        var client = await _host.SignInAsync("alice", "alice-password-1");

        var (initial, deltaLink, _) = await SyncAsync(client, $"{ListUrl}/items/delta");
        Assert.Equal(["Plan", "Public", "Shared"], initial.Select(Title).Order(StringComparer.Ordinal));

        // Changes to items alice cannot see are not reported, not even as removed.
        var secret = await CreateAsync(_admin, "Secret", folder);
        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(_admin, $"{ListUrl}/items/{secret}"));
        var visible = await CreateAsync(_admin, "Visible");
        var (changes, next, _) = await SyncAsync(client, deltaLink);
        Assert.Equal([visible], changes.Where(e => !Removed(e)).Select(e => e.Id()));
        Assert.DoesNotContain(secret, changes.Select(e => e.Id()));

        // Losing access removes the items (ADR-0035): the folder and the document inside it, no 410.
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"{ListUrl}/items/{shared}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        (changes, next, _) = await SyncAsync(client, next);
        Assert.Equal(
            new[] { $"{plan}:changed", $"{shared}:changed" }.Order(StringComparer.Ordinal),
            changes.Select(e => $"{e.Id()}:{e.GetProperty("@removed").GetProperty("reason").GetString()}").Order(StringComparer.Ordinal));

        // Resetting a folder brings its items back; a grant to alice on a scope returns the scope's items.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"{ListUrl}/items/{folder}/permissions/resetInheritance", null, Ct)).StatusCode);
        (changes, next, _) = await SyncAsync(client, next);
        Assert.Equal(["Private"], changes.Select(Title));
        var put = await _admin.PutAsJsonAsync($"{ListUrl}/items/{shared}/permissions/grants",
            new { grants = new[] { new { principalType = "user", principalId = aliceId, level = "read" } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        (changes, _, _) = await SyncAsync(client, next);
        Assert.Equal(["Plan", "Shared"], changes.Select(Title).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Delta_rejects_bad_tokens_and_other_tenants()
    {
        await CreateAsync(_admin, "A");
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync($"{ListUrl}/items/delta?$deltatoken=nonsense", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync($"{ListUrl}/items/delta?$top=0", Ct)).StatusCode);

        var (_, deltaLink, _) = await SyncAsync(_admin, $"{ListUrl}/items/delta");
        var other = await _host.CreateTenantAsync("other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{ListUrl}/items/delta", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(deltaLink, Ct)).StatusCode);
    }
}
