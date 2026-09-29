using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>Delta sync of lists (API-05).</summary>
public sealed class DeltaTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(HttpClient Admin, string Tenant, Guid Workspace, Guid List)
    {
        public string ListUrl => $"/v1.0/workspaces/{Workspace}/lists/{List}";
    }

    private async Task<Setup> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var admin = await ApiClient.CreateAsync(factory, tenant);
        var ws = await admin.CreateWorkspaceAsync("Sync");
        var contentType = await admin.CreateContentTypeAsync("Note", [new { name = "text", type = "text" }]);
        return new Setup(admin, tenant, ws, await admin.CreateListAsync(ws, "Notes", contentType));
    }

    private static async Task<Guid> CreateAsync(HttpClient client, Setup setup, string title, Guid? parentId = null, bool isFolder = false) =>
        (await client.CreateItemAsync(setup.Workspace, setup.List, new { parentId, isFolder, fields = new { title } })).GetProperty("id").GetGuid();

    /// <summary>Follows nextLinks; returns all entries and the deltaLink.</summary>
    private static async Task<(List<JsonElement> Entries, string DeltaLink, int Pages)> SyncAsync(HttpClient client, string url)
    {
        var entries = new List<JsonElement>();
        for (var pages = 1; ; pages++)
        {
            var response = await client.GetAsync(url, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
            var body = await response.ReadJsonAsync();
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
        return (await client.SendWithEtagAsync(HttpMethod.Delete, url, etag)).StatusCode;
    }

    private static string Title(JsonElement entry) => entry.GetProperty("fields").GetProperty("title").GetString()!;

    private static bool Removed(JsonElement entry) => entry.TryGetProperty("@removed", out _);

    [Fact]
    public async Task Delta_returns_all_items_first_and_then_only_changes()
    {
        var setup = await SetupAsync("delta-basic");
        var a = await CreateAsync(setup.Admin, setup, "A");
        var b = await CreateAsync(setup.Admin, setup, "B");
        await CreateAsync(setup.Admin, setup, "C");

        var (initial, deltaLink, pages) = await SyncAsync(setup.Admin, $"{setup.ListUrl}/items/delta?$top=2");
        Assert.Equal(2, pages);
        Assert.Equal(["A", "B", "C"], initial.Select(Title).Order(StringComparer.Ordinal));

        var (none, same, _) = await SyncAsync(setup.Admin, deltaLink);
        Assert.Empty(none);

        var etag = (await setup.Admin.GetAsync($"{setup.ListUrl}/items/{a}", Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await setup.Admin.SendWithEtagAsync(HttpMethod.Patch, $"{setup.ListUrl}/items/{a}", etag, new { fields = new { title = "A2" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(setup.Admin, $"{setup.ListUrl}/items/{b}"));
        var d = await CreateAsync(setup.Admin, setup, "D");

        var (changes, next, _) = await SyncAsync(setup.Admin, same);
        Assert.Equal(3, changes.Count);
        Assert.Equal("A2", Title(changes.Single(e => e.GetProperty("id").GetGuid() == a)));
        Assert.True(Removed(changes.Single(e => e.GetProperty("id").GetGuid() == b)));
        Assert.Equal("D", Title(changes.Single(e => e.GetProperty("id").GetGuid() == d)));

        Assert.Empty((await SyncAsync(setup.Admin, next)).Entries);

        // Restoring from the recycle bin brings the item back.
        Assert.Equal(HttpStatusCode.OK, (await setup.Admin.PostAsync($"{setup.ListUrl}/recycleBin/{b}/restore", null, Ct)).StatusCode);
        var restored = (await SyncAsync(setup.Admin, next)).Entries;
        Assert.Equal("B", Title(Assert.Single(restored)));
    }

    [Fact]
    public async Task Delta_follows_item_permissions_without_a_resync()
    {
        var setup = await SetupAsync("delta-permissions");
        var alice = await setup.Admin.PostAsJsonAsync("/v1.0/users", new { userName = "alice", password = "alice-password-1" }, Ct);
        await setup.Admin.PostAsJsonAsync($"/v1.0/workspaces/{setup.Workspace}/members",
            new { userId = (await alice.ReadJsonAsync()).GetProperty("id").GetGuid(), role = "member" }, Ct);
        var folder = await CreateAsync(setup.Admin, setup, "Private", isFolder: true);
        Assert.Equal(HttpStatusCode.OK, (await setup.Admin.PostAsJsonAsync($"{setup.ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        var shared = await CreateAsync(setup.Admin, setup, "Shared", isFolder: true);
        var plan = await CreateAsync(setup.Admin, setup, "Plan", shared);
        await CreateAsync(setup.Admin, setup, "Public");
        var client = await ApiClient.CreateAsync(factory, setup.Tenant, "alice", "alice-password-1");

        var (initial, deltaLink, _) = await SyncAsync(client, $"{setup.ListUrl}/items/delta");
        Assert.Equal(["Plan", "Public", "Shared"], initial.Select(Title).Order(StringComparer.Ordinal));

        // Changes to items alice cannot see are not reported, not even as removed.
        var secret = await CreateAsync(setup.Admin, setup, "Secret", folder);
        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(setup.Admin, $"{setup.ListUrl}/items/{secret}"));
        var visible = await CreateAsync(setup.Admin, setup, "Visible");
        var (changes, next, _) = await SyncAsync(client, deltaLink);
        Assert.Equal([visible], changes.Where(e => !e.TryGetProperty("@removed", out _)).Select(e => e.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(secret, changes.Select(e => e.GetProperty("id").GetGuid()));

        // Losing access removes the items (ADR-0035): the folder and the document inside it, no 410.
        Assert.Equal(HttpStatusCode.OK, (await setup.Admin.PostAsJsonAsync($"{setup.ListUrl}/items/{shared}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        (changes, next, _) = await SyncAsync(client, next);
        Assert.Equal(
            new[] { $"{plan}:changed", $"{shared}:changed" }.Order(StringComparer.Ordinal),
            changes.Select(e => $"{e.GetProperty("id").GetGuid()}:{e.GetProperty("@removed").GetProperty("reason").GetString()}").Order(StringComparer.Ordinal));

        // Resetting a folder brings its items back; a grant to alice on a scope returns the scope's items.
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Admin.PostAsync($"{setup.ListUrl}/items/{folder}/permissions/resetInheritance", null, Ct)).StatusCode);
        (changes, next, _) = await SyncAsync(client, next);
        Assert.Equal(["Private"], changes.Select(Title));
        var aliceId = (await (await client.GetAsync("/v1.0/me", Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var put = await setup.Admin.PutAsJsonAsync($"{setup.ListUrl}/items/{shared}/permissions/grants",
            new { grants = new[] { new { principalType = "user", principalId = aliceId, level = "read" } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        (changes, _, _) = await SyncAsync(client, next);
        Assert.Equal(["Plan", "Shared"], changes.Select(Title).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Delta_rejects_bad_tokens_and_other_tenants()
    {
        var setup = await SetupAsync("delta-isolation-a");
        await CreateAsync(setup.Admin, setup, "A");
        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Admin.GetAsync($"{setup.ListUrl}/items/delta?$deltatoken=nonsense", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Admin.GetAsync($"{setup.ListUrl}/items/delta?$top=0", Ct)).StatusCode);

        var (_, deltaLink, _) = await SyncAsync(setup.Admin, $"{setup.ListUrl}/items/delta");
        await factory.CreateTenantAsync("delta-isolation-b");
        var other = await ApiClient.CreateAsync(factory, "delta-isolation-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{setup.ListUrl}/items/delta", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(deltaLink, Ct)).StatusCode);
    }
}
