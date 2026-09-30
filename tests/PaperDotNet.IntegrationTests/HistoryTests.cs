using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Jobs.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Version history, recycle bin and their audit entries (LST-11/12/13).</summary>
public sealed class HistoryTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Retention 0: the cleanup job purges everything in the recycle bin when it runs (the test runs it by hand).
    private readonly TestHost _host = new(settings: new Dictionary<string, string> { ["Lists:RecycleBinRetentionDays"] = "0" });

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<(string Workspace, string List)> SetupAsync(HttpClient client, object listSettings)
    {
        var workspace = await Api.CreateWorkspaceAsync(client, "Records");
        var contentType = await Api.CreateContentTypeAsync(client, "Record", new[] { new { name = "status", type = "text" }, new { name = "note", type = "text" } });
        var request = new Dictionary<string, object?> { ["name"] = "Records", ["contentTypeIds"] = new[] { contentType } };
        foreach (var property in JsonSerializer.SerializeToElement(listSettings).EnumerateObject())
        {
            request[property.Name] = property.Value;
        }

        using var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists", request, Ct);
        return (workspace, (await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private static async Task<HttpResponseMessage> PatchItemAsync(HttpClient client, string url, object fields)
    {
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        return await client.SendAsync(Api.Patch(url, new { fields }, etag), Ct);
    }

    private static async Task DeleteItemAsync(HttpClient client, string url)
    {
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Api.WithETag(HttpMethod.Delete, url, null, etag), Ct)).StatusCode);
    }

    private static async Task<List<JsonElement>> ValuesAsync(HttpClient client, string url) =>
        [.. (await (await client.GetAsync(url, Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()];

    private static async Task<List<string>> TitlesAsync(HttpClient client, string ws, string list) =>
        [.. (await ValuesAsync(client, Api.Items(ws, list))).Select(i => i.GetProperty("fields").GetProperty("title").GetString()!)];

    [Fact]
    public async Task Versioned_lists_keep_trimmed_history_and_restore_versions()
    {
        var client = await _host.SignInAsync();
        var (ws, list) = await SetupAsync(client, new { versioning = "major", maxVersions = 3 });
        var item = await Api.CreateItemAsync(client, ws, list, new { title = "R-1", status = "draft", note = "first" });
        var url = $"{Api.Items(ws, list)}/{item.Id()}";
        await PatchItemAsync(client, url, new { status = "review" });
        await PatchItemAsync(client, url, new { status = "final", note = (string?)null });
        await PatchItemAsync(client, url, new { title = "R-1 final" });
        Assert.Equal(HttpStatusCode.OK, (await PatchItemAsync(client, url, new { status = "final" })).StatusCode); // no change: no version

        var versions = await ValuesAsync(client, $"{url}/versions");
        Assert.Equal([4, 3, 2], versions.Select(v => v.GetProperty("number").GetInt32()));
        Assert.True(versions[0].GetProperty("isCurrent").GetBoolean());
        Assert.False(versions[1].GetProperty("isCurrent").GetBoolean());
        Assert.Equal(["note", "status"], versions[1].GetProperty("changedFields").EnumerateArray().Select(f => f.GetString()));

        var v2 = await (await client.GetAsync($"{url}/versions/2", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("review", v2.GetProperty("fields").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{url}/versions/1", Ct)).StatusCode);

        // Paging walks the versions newest first.
        var first = await (await client.GetAsync($"{url}/versions?$top=2", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(2, first.GetProperty("value").GetArrayLength());
        var second = await (await client.GetAsync(first.GetProperty("@odata.nextLink").GetString(), Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(2, Assert.Single(second.GetProperty("value").EnumerateArray()).GetProperty("number").GetInt32());

        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.PostAsync($"{url}/versions/2/restore", null, Ct)).StatusCode);
        using var restored = await client.SendAsync(Api.WithETag(HttpMethod.Post, $"{url}/versions/2/restore", null, etag), Ct);
        var fields = (await restored.JsonAsync(HttpStatusCode.OK)).GetProperty("fields");
        Assert.Equal("R-1", fields.GetProperty("title").GetString());
        Assert.Equal("review", fields.GetProperty("status").GetString());
        Assert.Equal("first", fields.GetProperty("note").GetString());
        Assert.Equal(5, (await ValuesAsync(client, $"{url}/versions"))[0].GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task Lists_without_versioning_keep_no_versions()
    {
        var client = await _host.SignInAsync();
        var (ws, list) = await SetupAsync(client, new { });
        var listBody = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("off", listBody.GetProperty("versioning").GetString());

        var item = await Api.CreateItemAsync(client, ws, list, new { title = "R-1" });
        var url = $"{Api.Items(ws, list)}/{item.Id()}";
        await PatchItemAsync(client, url, new { status = "x" });

        Assert.Empty(await ValuesAsync(client, $"{url}/versions"));
    }

    [Fact]
    public async Task Deleted_items_go_to_the_recycle_bin_and_can_be_restored_or_purged()
    {
        var client = await _host.SignInAsync();
        var (ws, list) = await SetupAsync(client, new { versioning = "major" });
        var items = Api.Items(ws, list);
        var bin = $"/v1.0/workspaces/{ws}/lists/{list}/recycleBin";
        var folder = await (await client.PostAsJsonAsync(items, new { isFolder = true, fields = new { title = "Folder" } }, Ct)).JsonAsync(HttpStatusCode.Created);
        var inFolder = await (await client.PostAsJsonAsync(items, new { parentId = folder.Id(), fields = new { title = "In folder" } }, Ct)).JsonAsync(HttpStatusCode.Created);
        var other = await Api.CreateItemAsync(client, ws, list, new { title = "Other" });

        await DeleteItemAsync(client, $"{items}/{inFolder.Id()}");
        await DeleteItemAsync(client, $"{items}/{folder.Id()}");
        await DeleteItemAsync(client, $"{items}/{other.Id()}");
        Assert.Empty(await TitlesAsync(client, ws, list));
        var deleted = await ValuesAsync(client, bin);
        Assert.Equal(3, deleted.Count);
        Assert.All(deleted, b => Assert.True(b.TryGetProperty("deletedAt", out _)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{items}/{other.Id()}", Ct)).StatusCode);

        // The folder is still deleted, so the item comes back at the root.
        using (var restored = await client.PostAsync($"{bin}/{inFolder.Id()}/restore", null, Ct))
        {
            Assert.Equal(JsonValueKind.Null, (await restored.JsonAsync(HttpStatusCode.OK)).GetProperty("parentId").ValueKind);
        }

        Assert.Equal(["In folder"], await TitlesAsync(client, ws, list));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{bin}/{other.Id()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{bin}/{other.Id()}/restore", null, Ct)).StatusCode);
        Assert.Single(await ValuesAsync(client, bin));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{bin}/{inFolder.Id()}", Ct)).StatusCode);

        // Restoring, then purging, is in the audit log.
        var audit = await WaitForAuditAsync(client, inFolder.Id(), 3);
        Assert.Equal(["item.restored", "item.deleted", "item.created"], audit);
        Assert.Contains("item.purged", await WaitForAuditAsync(client, other.Id(), 3));
    }

    [Fact]
    public async Task Expired_recycle_bin_items_are_purged_with_their_versions()
    {
        var client = await _host.SignInAsync();
        var (ws, list) = await SetupAsync(client, new { versioning = "major" });
        var item = await Api.CreateItemAsync(client, ws, list, new { title = "Old" });
        var kept = await Api.CreateItemAsync(client, ws, list, new { title = "Kept" });
        var url = $"{Api.Items(ws, list)}/{item.Id()}";
        await DeleteItemAsync(client, url);

        var tenantId = Guid.Parse((await (await client.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var registration = scope.ServiceProvider.GetServices<RecurringJobRegistration>().Single(r => r.Name == "lists.recycle-bin-cleanup");
            await registration.Create(scope.ServiceProvider).RunAsync(tenantId, Ct);
        }

        Assert.Empty(await ValuesAsync(client, $"/v1.0/workspaces/{ws}/lists/{list}/recycleBin"));
        Assert.Equal(["Kept"], await TitlesAsync(client, ws, list));
        Assert.Single(await ValuesAsync(client, $"{Api.Items(ws, list)}/{kept.Id()}/versions"));
    }

    [Fact]
    public async Task The_recycle_bin_is_for_contributors_of_the_list_only()
    {
        var admin = await _host.SignInAsync();
        var (ws, list) = await SetupAsync(admin, new { });
        var item = await Api.CreateItemAsync(admin, ws, list, new { title = "Secret" });
        await DeleteItemAsync(admin, $"{Api.Items(ws, list)}/{item.Id()}");
        var bin = $"/v1.0/workspaces/{ws}/lists/{list}/recycleBin";

        using (var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct))
        {
            var memberId = (await created.JsonAsync(HttpStatusCode.Created)).Id();
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = memberId, role = "visitor" }, Ct)).StatusCode);
        }

        var visitor = await _host.SignInAsync("member", "member-password-1");
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(bin, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.PostAsync($"{bin}/{item.Id()}/restore", null, Ct)).StatusCode);

        var other = await _host.CreateTenantAsync("other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(bin, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Api.Items(ws, list)}/{item.Id()}/versions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{bin}/{item.Id()}/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{bin}/{item.Id()}", Ct)).StatusCode);
        Assert.Single(await ValuesAsync(admin, bin));
    }

    /// <summary>The audit actions for an item, newest first, once there are <paramref name="count"/>.</summary>
    private static async Task<List<string>> WaitForAuditAsync(HttpClient client, string itemId, int count)
    {
        List<string> actions = [];
        for (var attempt = 0; attempt < 100 && actions.Count < count; attempt++)
        {
            actions = [.. (await ValuesAsync(client, $"/v1.0/audit?targetId={itemId}")).Select(e => e.GetProperty("action").GetString()!)];
            if (actions.Count < count)
            {
                await Task.Delay(100, Ct);
            }
        }

        return actions;
    }
}
