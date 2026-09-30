using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;

namespace PaperDotNet.IntegrationTests;

/// <summary>Version history, recycle bin and audit log (phase 1e).</summary>
public sealed class HistoryTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid _tenantId;

    private async Task<(HttpClient Client, Guid Workspace, Guid List)> SetupAsync(string tenant, object listSettings)
    {
        _tenantId = (await factory.CreateTenantAsync(tenant)).Id;
        var client = await ApiClient.CreateAsync(factory, tenant);
        var workspace = await client.CreateWorkspaceAsync("Records");
        var contentType = await client.CreateContentTypeAsync("Record", [new { name = "status", type = "text" }, new { name = "note", type = "text" }]);
        var body = JsonSerializer.SerializeToElement(listSettings, ApiClient.Json);
        var request = new Dictionary<string, object?> { ["name"] = "Records", ["contentTypeIds"] = new[] { contentType } };
        foreach (var property in body.EnumerateObject())
        {
            request[property.Name] = property.Value;
        }

        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists", request, Ct);
        response.EnsureSuccessStatusCode();
        return (client, workspace, (await response.ReadJsonAsync()).GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> PatchItemAsync(HttpClient client, string url, object fields)
    {
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        return await client.SendWithEtagAsync(HttpMethod.Patch, url, etag, new { fields });
    }

    private static async Task<List<JsonElement>> ValuesAsync(HttpClient client, string url) =>
        (await (await client.GetAsync(url, Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();

    [Fact]
    public async Task Versioned_lists_keep_trimmed_history_and_restore_versions()
    {
        var (client, ws, list) = await SetupAsync("history-versions", new { versioning = "major", maxVersions = 3 });
        var item = await client.CreateItemAsync(ws, list, new { fields = new { title = "R-1", status = "draft", note = "first" } });
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item.GetProperty("id").GetGuid()}";
        await PatchItemAsync(client, url, new { status = "review" });
        await PatchItemAsync(client, url, new { status = "final", note = (string?)null });
        await PatchItemAsync(client, url, new { title = "R-1 final" });
        Assert.Equal(HttpStatusCode.OK, (await PatchItemAsync(client, url, new { status = "final" })).StatusCode); // no change → no version

        var versions = await ValuesAsync(client, $"{url}/versions");
        Assert.Equal([4, 3, 2], versions.Select(v => v.GetProperty("number").GetInt32()));
        Assert.True(versions[0].GetProperty("isCurrent").GetBoolean());
        Assert.Equal(["note", "status"], versions[1].GetProperty("changedFields").EnumerateArray().Select(f => f.GetString()));

        var v2 = await (await client.GetAsync($"{url}/versions/2", Ct)).ReadJsonAsync();
        Assert.Equal("review", v2.GetProperty("fields").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{url}/versions/1", Ct)).StatusCode);

        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.PostAsync($"{url}/versions/2/restore", null, Ct)).StatusCode);
        var restored = await client.SendWithEtagAsync(HttpMethod.Post, $"{url}/versions/2/restore", etag);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var fields = (await restored.ReadJsonAsync()).GetProperty("fields");
        Assert.Equal("R-1", fields.GetProperty("title").GetString());
        Assert.Equal("review", fields.GetProperty("status").GetString());
        Assert.Equal("first", fields.GetProperty("note").GetString());
        Assert.Equal(5, (await ValuesAsync(client, $"{url}/versions"))[0].GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task Lists_without_versioning_keep_no_versions()
    {
        var (client, ws, list) = await SetupAsync("history-off", new { });
        var listBody = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}", Ct)).ReadJsonAsync();
        Assert.Equal("off", listBody.GetProperty("versioning").GetString(), ignoreCase: true);

        var item = await client.CreateItemAsync(ws, list, new { fields = new { title = "R-1" } });
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item.GetProperty("id").GetGuid()}";
        await PatchItemAsync(client, url, new { status = "x" });

        Assert.Empty(await ValuesAsync(client, $"{url}/versions"));
    }

    [Fact]
    public async Task Deleted_items_go_to_the_recycle_bin_and_can_be_restored_or_purged()
    {
        var (client, ws, list) = await SetupAsync("history-bin", new { versioning = "major" });
        var itemsUrl = $"/v1.0/workspaces/{ws}/lists/{list}";
        var folder = await client.CreateItemAsync(ws, list, new { isFolder = true, fields = new { title = "Folder" } });
        var inFolder = await client.CreateItemAsync(ws, list, new { parentId = folder.GetProperty("id").GetGuid(), fields = new { title = "In folder" } });
        var other = await client.CreateItemAsync(ws, list, new { fields = new { title = "Other" } });

        async Task DeleteAsync(JsonElement item)
        {
            var url = $"{itemsUrl}/items/{item.GetProperty("id").GetGuid()}";
            var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendWithEtagAsync(HttpMethod.Delete, url, etag)).StatusCode);
        }

        await DeleteAsync(inFolder);
        await DeleteAsync(folder);
        await DeleteAsync(other);
        Assert.Empty(await client.QueryTitlesAsync(ws, list, ""));
        var bin = await ValuesAsync(client, $"{itemsUrl}/recycleBin");
        Assert.Equal(3, bin.Count);
        Assert.All(bin, b => Assert.True(b.TryGetProperty("deletedAt", out _)));

        // The folder is still deleted, so the item comes back at the root.
        var restored = await client.PostAsync($"{itemsUrl}/recycleBin/{inFolder.GetProperty("id").GetGuid()}/restore", null, Ct);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.False((await restored.ReadJsonAsync()).TryGetProperty("parentId", out var parent) && parent.ValueKind != JsonValueKind.Null);
        Assert.Equal(["In folder"], await client.QueryTitlesAsync(ws, list, ""));

        var otherId = other.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{itemsUrl}/recycleBin/{otherId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{itemsUrl}/recycleBin/{otherId}/restore", null, Ct)).StatusCode);
        Assert.Single(await ValuesAsync(client, $"{itemsUrl}/recycleBin"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{itemsUrl}/recycleBin/{inFolder.GetProperty("id").GetGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Expired_recycle_bin_items_are_purged_with_their_versions()
    {
        var tenant = "history-retention";
        var (client, ws, list) = await SetupAsync(tenant, new { versioning = "major" });
        var item = await client.CreateItemAsync(ws, list, new { fields = new { title = "Old" } });
        var itemId = item.GetProperty("id").GetGuid();
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items/{itemId}";
        await client.SendWithEtagAsync(HttpMethod.Delete, url, (await client.GetAsync(url, Ct)).Headers.ETag!.Tag);

        var tenantId = _tenantId;
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenantId, tenant))
        {
            var services = scope.ServiceProvider;
            var later = new ShiftedTimeProvider(TimeSpan.FromDays(100));
            var job = new RecycleBinCleanupJob(
                services.GetRequiredService<ListsDbContext>(), services.GetRequiredService<ItemWriter>(), later, services.GetRequiredService<IOptions<ListsOptions>>());
            await job.RunAsync(Ct);
        }

        Assert.Empty(await ValuesAsync(client, $"/v1.0/workspaces/{ws}/lists/{list}/recycleBin"));
        await using var check = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenantId, tenant);
        var db = check.ServiceProvider.GetRequiredService<ListsDbContext>();
        Assert.False(db.ItemVersions.Any(v => v.ItemId == itemId));
    }

    [Fact]
    public async Task Changes_are_recorded_in_the_audit_log()
    {
        var (client, ws, list) = await SetupAsync("history-audit", new { });
        var item = await client.CreateItemAsync(ws, list, new { fields = new { title = "Audited" } });
        var itemId = item.GetProperty("id").GetGuid();
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items/{itemId}";
        await PatchItemAsync(client, url, new { status = "done" });
        await client.SendWithEtagAsync(HttpMethod.Delete, url, (await client.GetAsync(url, Ct)).Headers.ETag!.Tag);
        await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/recycleBin/{itemId}/restore", null, Ct);

        var entries = await ValuesAsync(client, $"/v1.0/auditLog?entityId={itemId}");
        Assert.Equal(["Restored", "Deleted", "Updated", "Created"], entries.Select(e => e.GetProperty("action").GetString()!).Select(Capitalize));
        Assert.Equal("lists.ListItem", entries[0].GetProperty("entityType").GetString());
        Assert.Contains("Fields", entries[2].GetProperty("properties").EnumerateArray().Select(p => p.GetString()));
        Assert.All(entries, e => Assert.NotEqual(JsonValueKind.Null, e.GetProperty("userId").ValueKind));

        // Paging walks across the audit tables of all modules without gaps or repeats.
        var all = new List<Guid>();
        string? next = "/v1.0/auditLog?$top=3";
        while (next is not null)
        {
            var page = await (await client.GetAsync(next, Ct)).ReadJsonAsync();
            all.AddRange(page.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
            next = page.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;
        }

        var unpaged = (await ValuesAsync(client, "/v1.0/auditLog?$top=500")).Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(unpaged, all);
        Assert.Contains(await ValuesAsync(client, "/v1.0/auditLog?entityType=workspaces.Workspace"), e => e.GetProperty("action").GetString() is "created" or "Created");
    }

    [Fact]
    public async Task Audit_log_and_recycle_bin_are_protected()
    {
        var (admin, ws, list) = await SetupAsync("history-access", new { });
        var item = await admin.CreateItemAsync(ws, list, new { fields = new { title = "Secret" } });
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item.GetProperty("id").GetGuid()}";
        await admin.SendWithEtagAsync(HttpMethod.Delete, url, (await admin.GetAsync(url, Ct)).Headers.ETag!.Tag);
        await admin.PostAsJsonAsync("/v1.0/users", new { userName = "member", password = "member-password-1" }, Ct);
        var member = await ApiClient.CreateAsync(factory, "history-access", "member", "member-password-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/v1.0/auditLog", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/recycleBin", Ct)).StatusCode);

        await factory.CreateTenantAsync("history-access-b");
        var other = await ApiClient.CreateAsync(factory, "history-access-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/recycleBin", Ct)).StatusCode);
        var foreign = await ValuesAsync(other, $"/v1.0/auditLog?entityId={item.GetProperty("id").GetGuid()}");
        Assert.Empty(foreign);
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private sealed class ShiftedTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }
}
