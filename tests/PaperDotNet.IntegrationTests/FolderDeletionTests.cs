using System.Net;

namespace PaperDotNet.IntegrationTests;

/// <summary>PaperGo parity: explicit, atomic deletion of populated folders.</summary>
public sealed class FolderDeletionTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Recursive_deletion_recycles_the_whole_tree_and_preserves_retained_history()
    {
        await factory.CreateTenantAsync("folder-delete-tree");
        var client = await ApiClient.CreateAsync(factory, "folder-delete-tree");
        var ws = await client.CreateWorkspaceAsync("Records");
        var list = await client.CreateListAsync(ws, "Records");
        var listUrl = $"/v1.0/workspaces/{ws}/lists/{list}";
        var listEtag = (await client.GetAsync(listUrl, Ct)).Headers.ETag!.Tag;
        (await client.SendWithEtagAsync(HttpMethod.Patch, listUrl, listEtag, new { versioning = "major" })).EnsureSuccessStatusCode();

        async Task<Guid> CreateAsync(string title, Guid? parentId = null, bool isFolder = false) =>
            (await client.CreateItemAsync(ws, list, new { isFolder, parentId, fields = new { title } })).GetProperty("id").GetGuid();

        var root = await CreateAsync("Root", isFolder: true);
        var sub = await CreateAsync("Subfolder", root, true);
        var child = await CreateAsync("Document", sub);
        var sibling = await CreateAsync("Sibling", root);
        var outside = await CreateAsync("Outside");
        var url = $"{listUrl}/items/{root}";
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendWithEtagAsync(HttpMethod.Delete, url, etag)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.DeleteAsync($"{url}?recursive=true", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendWithEtagAsync(HttpMethod.Delete, $"{url}?recursive=true", "\"0\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendWithEtagAsync(HttpMethod.Delete, $"{url}?recursive=true", etag)).StatusCode);

        foreach (var id in new[] { root, sub, child, sibling })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{listUrl}/items/{id}", Ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{listUrl}/items/{outside}", Ct)).StatusCode);
        var bin = (await (await client.GetAsync($"{listUrl}/recycleBin", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal(4, bin.Count);
        Assert.Single(bin.Select(entry => entry.GetProperty("deletedAt").GetString()).Distinct());

        // Restoring retained entries keeps their identities, location and history.
        foreach (var id in new[] { root, sub, child })
        {
            (await client.PostAsync($"{listUrl}/recycleBin/{id}/restore", null, Ct)).EnsureSuccessStatusCode();
        }

        var restored = await (await client.GetAsync($"{listUrl}/items/{child}", Ct)).ReadJsonAsync();
        Assert.Equal(sub, restored.GetProperty("parentId").GetGuid());
        Assert.Equal("Document", restored.GetProperty("fields").GetProperty("title").GetString());
        var versions = await (await client.GetAsync($"{listUrl}/items/{child}/versions", Ct)).ReadJsonAsync();
        Assert.Single(versions.GetProperty("value").EnumerateArray());

        await factory.CreateTenantAsync("folder-delete-other");
        var other = await ApiClient.CreateAsync(factory, "folder-delete-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendWithEtagAsync(HttpMethod.Delete, $"{url}?recursive=true", etag)).StatusCode);
    }

    [Fact]
    public async Task A_descendant_mutator_veto_leaves_every_item_and_version_unchanged()
    {
        await factory.CreateTenantAsync("folder-delete-veto");
        var client = await ApiClient.CreateAsync(factory, "folder-delete-veto");
        var ws = await client.CreateWorkspaceAsync("Records");
        var list = await client.CreateListAsync(ws, "Hooked");
        var root = await client.CreateItemAsync(ws, list, new { isFolder = true, fields = new { title = "Folder" } });
        var parentId = root.GetProperty("id").GetGuid();
        var ordinary = await client.CreateItemAsync(ws, list, new { parentId, fields = new { title = "Ordinary" } });
        var protectedItem = await client.CreateItemAsync(ws, list, new { parentId, fields = new { title = "keep forever" } });
        var baseUrl = $"/v1.0/workspaces/{ws}/lists/{list}";
        var url = $"{baseUrl}/items/{parentId}";
        var etag = (await client.GetAsync(url, Ct)).Headers.ETag!.Tag;

        var rejected = await client.SendWithEtagAsync(HttpMethod.Delete, $"{url}?recursive=true", etag);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("cancelledByMutator", (await rejected.ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal(etag, (await client.GetAsync(url, Ct)).Headers.ETag!.Tag);
        Assert.Equal(3, (await client.QueryTitlesAsync(ws, list, "")).Count);
        Assert.Empty((await (await client.GetAsync($"{baseUrl}/recycleBin", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray());
        foreach (var entry in new[] { ordinary, protectedItem })
        {
            var current = await (await client.GetAsync($"{baseUrl}/items/{entry.GetProperty("id").GetGuid()}", Ct)).ReadJsonAsync();
            Assert.Equal(entry.GetProperty("@odata.etag").GetString(), current.GetProperty("@odata.etag").GetString());
        }
    }
}
