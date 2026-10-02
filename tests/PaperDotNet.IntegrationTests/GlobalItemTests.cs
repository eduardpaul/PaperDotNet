using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Persistence;

namespace PaperDotNet.IntegrationTests;

public sealed class GlobalItemTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Module_context_can_be_reused_after_a_shared_transaction(bool fail)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
        var module = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var original = module.Database.GetDbConnection();
        await module.FileVersions.IgnoreQueryFilters().CountAsync(Ct);
        await using (var transaction = await owner.Database.BeginTransactionAsync(Ct))
        {
            async Task ActionAsync(CancellationToken ct)
            {
                await module.FileVersions.IgnoreQueryFilters().CountAsync(ct);
                if (fail)
                    throw new InvalidOperationException("Test failure.");
            }

            var action = SharedTransaction.RunAsync(module, transaction.GetDbTransaction(), ActionAsync, Ct);
            if (fail)
                await Assert.ThrowsAsync<InvalidOperationException>(() => action);
            else
                await action;
            await transaction.RollbackAsync(Ct);
        }

        Assert.NotSame(original, module.Database.GetDbConnection());
        await module.FileVersions.IgnoreQueryFilters().CountAsync(Ct);
        await owner.Items.IgnoreQueryFilters().CountAsync(Ct);
    }

    private sealed record Setup(HttpClient Client, Guid Workspace, Guid List, Guid DestinationWorkspace, Guid DestinationList, Guid First, Guid Second)
    {
        public string SourceUrl => $"/v1.0/workspaces/{Workspace}/lists/{List}/items/{First}";

        public string DestinationUrl => $"/v1.0/workspaces/{DestinationWorkspace}/lists/{DestinationList}/items/{First}";

        public string RelationUrl => $"/v1.0/items/{First}/relations/{Second}";
    }

    private async Task<Setup> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var client = await ApiClient.CreateAsync(factory, tenant);
        var workspace = await client.CreateWorkspaceAsync("Source");
        var destination = await client.CreateWorkspaceAsync("Destination");
        var contentType = await client.CreateContentTypeAsync("Record", [new { name = "code", type = "text", indexed = true }]);
        var otherType = await client.CreateContentTypeAsync("Other record", [new { name = "misc", type = "text", indexed = true }]);
        var list = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists",
            new { name = "Records", contentTypeIds = new[] { contentType }, versioning = "major" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var target = await client.CreateListAsync(destination, "Filed records", otherType, contentType);
        var first = (await client.CreateItemAsync(workspace, list, new { contentTypeId = contentType, fields = new { title = "First record", code = "ABC" } })).GetProperty("id").GetGuid();
        var second = (await client.CreateItemAsync(destination, target, new { contentTypeId = otherType, fields = new { title = "Second record" } })).GetProperty("id").GetGuid();
        return new Setup(client, workspace, list, destination, target, first, second);
    }

    private static async Task<List<JsonElement>> RelatedAsync(HttpClient client, Guid id) =>
        (await (await client.GetAsync($"/v1.0/items/{id}/relations", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();

    private static async Task<HttpResponseMessage> MoveAsync(Setup setup)
    {
        var etag = (await setup.Client.GetAsync($"/v1.0/items/{setup.First}", Ct)).Headers.ETag!.Tag;
        return await setup.Client.SendWithEtagAsync(HttpMethod.Post, $"/v1.0/items/{setup.First}/move", etag,
            new { workspaceId = setup.DestinationWorkspace, listId = setup.DestinationList });
    }

    [Fact]
    public async Task Relationships_are_symmetric_multi_value_and_idempotent_across_content_types()
    {
        var setup = await SetupAsync("global-relations");
        var third = (await setup.Client.CreateItemAsync(setup.Workspace, setup.List, new { fields = new { title = "Third record" } })).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.PutAsync(setup.RelationUrl, null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.PutAsync($"/v1.0/items/{setup.Second}/relations/{setup.First}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.PutAsync($"/v1.0/items/{setup.First}/relations/{third}", null, Ct)).StatusCode);
        Assert.Equal(2, (await RelatedAsync(setup.Client, setup.First)).Count);
        Assert.Equal(setup.First, Assert.Single(await RelatedAsync(setup.Client, setup.Second)).GetProperty("item").GetProperty("id").GetGuid());

        var firstPage = await (await setup.Client.GetAsync($"/v1.0/items/{setup.First}/relations?$top=1", Ct)).ReadJsonAsync();
        Assert.Single(firstPage.GetProperty("value").EnumerateArray());
        var next = await (await setup.Client.GetAsync(firstPage.GetProperty("@odata.nextLink").GetString(), Ct)).ReadJsonAsync();
        Assert.Single(next.GetProperty("value").EnumerateArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Client.PutAsync($"/v1.0/items/{setup.First}/relations/{setup.First}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.DeleteAsync($"/v1.0/items/{setup.Second}/relations/{setup.First}", Ct)).StatusCode);
        Assert.Empty(await RelatedAsync(setup.Client, setup.Second));
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.DeleteAsync(setup.RelationUrl, Ct)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_inverse_creates_store_one_relationship()
    {
        var setup = await SetupAsync("global-concurrent");
        var responses = await Task.WhenAll(
            setup.Client.PutAsync(setup.RelationUrl, null, Ct),
            setup.Client.PutAsync($"/v1.0/items/{setup.Second}/relations/{setup.First}", null, Ct));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        Assert.Single(await RelatedAsync(setup.Client, setup.First));
        Assert.Single(await RelatedAsync(setup.Client, setup.Second));
    }

    [Fact]
    public async Task Moves_preserve_identity_relationships_history_and_delta_and_reindex_fields()
    {
        var setup = await SetupAsync("global-move");
        (await setup.Client.PutAsync(setup.RelationUrl, null, Ct)).EnsureSuccessStatusCode();
        var original = await (await setup.Client.GetAsync(setup.SourceUrl, Ct)).ReadJsonAsync();
        var sourceDelta = await (await setup.Client.GetAsync($"/v1.0/workspaces/{setup.Workspace}/lists/{setup.List}/items/delta", Ct)).ReadJsonAsync();
        var targetDelta = await (await setup.Client.GetAsync($"/v1.0/workspaces/{setup.DestinationWorkspace}/lists/{setup.DestinationList}/items/delta", Ct)).ReadJsonAsync();

        var moved = await MoveAsync(setup);
        Assert.True(moved.StatusCode == HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(Ct));
        var located = await moved.ReadJsonAsync();
        Assert.Equal(setup.First, located.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal(setup.DestinationWorkspace, located.GetProperty("workspaceId").GetGuid());
        Assert.Equal(original.GetProperty("createdAt").GetString(), located.GetProperty("item").GetProperty("createdAt").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Client.GetAsync(setup.SourceUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await setup.Client.GetAsync(setup.DestinationUrl, Ct)).StatusCode);
        Assert.Single(await RelatedAsync(setup.Client, setup.First));
        Assert.Equal(setup.DestinationList, Assert.Single(await RelatedAsync(setup.Client, setup.Second)).GetProperty("item").GetProperty("listId").GetGuid());
        var versions = await (await setup.Client.GetAsync($"{setup.DestinationUrl}/versions", Ct)).ReadJsonAsync();
        Assert.NotEmpty(versions.GetProperty("value").EnumerateArray());
        Assert.Contains("First record", await setup.Client.QueryTitlesAsync(setup.DestinationWorkspace, setup.DestinationList, "$filter=fields/code eq 'ABC'"));
        var removed = await (await setup.Client.GetAsync(sourceDelta.GetProperty("@odata.deltaLink").GetString(), Ct)).ReadJsonAsync();
        Assert.Equal("deleted", Assert.Single(removed.GetProperty("value").EnumerateArray()).GetProperty("@removed").GetProperty("reason").GetString());
        var added = await (await setup.Client.GetAsync(targetDelta.GetProperty("@odata.deltaLink").GetString(), Ct)).ReadJsonAsync();
        Assert.Contains(added.GetProperty("value").EnumerateArray(), i => i.GetProperty("id").GetGuid() == setup.First);
    }

    [Fact]
    public async Task Moves_validate_compatibility_destination_and_etags_before_changing_the_item()
    {
        var setup = await SetupAsync("global-move-validation");
        var url = $"/v1.0/items/{setup.First}/move";
        var body = new { workspaceId = setup.DestinationWorkspace, listId = setup.DestinationList };
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await setup.Client.PostAsJsonAsync(url, body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await setup.Client.SendWithEtagAsync(HttpMethod.Post, url, "\"0\"", body)).StatusCode);
        var incompatible = await setup.Client.CreateListAsync(setup.DestinationWorkspace, "Incompatible");
        var etag = (await setup.Client.GetAsync(setup.SourceUrl, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Client.SendWithEtagAsync(HttpMethod.Post, url, etag,
            new { workspaceId = setup.DestinationWorkspace, listId = incompatible })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Client.SendWithEtagAsync(HttpMethod.Post, url, etag,
            new { workspaceId = setup.DestinationWorkspace, listId = setup.DestinationList, parentId = setup.Second })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await setup.Client.GetAsync(setup.SourceUrl, Ct)).StatusCode);
    }

    [Fact]
    public async Task Recycle_restore_and_purge_have_defined_relationship_behavior()
    {
        var setup = await SetupAsync("global-lifecycle");
        (await setup.Client.PutAsync(setup.RelationUrl, null, Ct)).EnsureSuccessStatusCode();
        var etag = (await setup.Client.GetAsync(setup.SourceUrl, Ct)).Headers.ETag!.Tag;
        (await setup.Client.SendWithEtagAsync(HttpMethod.Delete, setup.SourceUrl, etag)).EnsureSuccessStatusCode();
        Assert.Empty(await RelatedAsync(setup.Client, setup.Second));
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Client.GetAsync($"/v1.0/items/{setup.First}", Ct)).StatusCode);
        var bin = $"/v1.0/workspaces/{setup.Workspace}/lists/{setup.List}/recycleBin/{setup.First}";
        (await setup.Client.PostAsync($"{bin}/restore", null, Ct)).EnsureSuccessStatusCode();
        Assert.Single(await RelatedAsync(setup.Client, setup.Second));
        etag = (await setup.Client.GetAsync(setup.SourceUrl, Ct)).Headers.ETag!.Tag;
        (await setup.Client.SendWithEtagAsync(HttpMethod.Delete, setup.SourceUrl, etag)).EnsureSuccessStatusCode();
        (await setup.Client.DeleteAsync(bin, Ct)).EnsureSuccessStatusCode();
        Assert.Empty(await RelatedAsync(setup.Client, setup.Second));
    }

    [Fact]
    public async Task Global_endpoints_enforce_tenant_isolation()
    {
        var setup = await SetupAsync("global-isolation-a");
        var other = await SetupAsync("global-isolation-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/v1.0/items/{setup.First}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/v1.0/items/{setup.First}/relations", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Client.PutAsync($"/v1.0/items/{setup.First}/relations/{other.First}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.DeleteAsync(setup.RelationUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync($"/v1.0/items/{setup.First}/move",
            new { workspaceId = other.Workspace, listId = other.List }, Ct)).StatusCode);
        var etag = (await setup.Client.GetAsync(setup.SourceUrl, Ct)).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Client.SendWithEtagAsync(HttpMethod.Post, $"/v1.0/items/{setup.First}/move", etag,
            new { workspaceId = other.Workspace, listId = other.List })).StatusCode);
        var page = await (await other.Client.GetAsync("/v1.0/items?q=FIRST", Ct)).ReadJsonAsync();
        Assert.Equal(other.First, Assert.Single(page.GetProperty("value").EnumerateArray()).GetProperty("item").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Relationships_are_security_trimmed_and_require_write_access_to_both_endpoints()
    {
        var setup = await SetupAsync("global-permissions");
        var user = await setup.Client.PostAsJsonAsync("/v1.0/users", new { userName = "reader", password = "reader-password-1" }, Ct);
        var userId = (await user.ReadJsonAsync()).GetProperty("id").GetGuid();
        foreach (var ws in new[] { setup.Workspace, setup.DestinationWorkspace })
        {
            (await setup.Client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId, role = "member" }, Ct)).EnsureSuccessStatusCode();
        }

        var reader = await ApiClient.CreateAsync(factory, "global-permissions", "reader", "reader-password-1");
        (await reader.PutAsync(setup.RelationUrl, null, Ct)).EnsureSuccessStatusCode();
        (await reader.PostAsJsonAsync($"/v1.0/items/{setup.First}/relationships", new { otherId = setup.Second, type = "references" }, Ct)).EnsureSuccessStatusCode();
        var graph = (await (await reader.GetAsync($"/v1.0/items/{setup.First}/relationships", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
        Assert.Equal(2, graph.Count);
        var typedEdge = graph.Single(e => e.TryGetProperty("type", out _)).GetProperty("id").GetGuid();
        var targetUrl = $"/v1.0/workspaces/{setup.DestinationWorkspace}/lists/{setup.DestinationList}/items/{setup.Second}";
        (await setup.Client.PostAsJsonAsync($"{targetUrl}/permissions/breakInheritance", new { copyGrants = false }, Ct)).EnsureSuccessStatusCode();
        Assert.Empty(await RelatedAsync(reader, setup.First));
        Assert.Empty((await (await reader.GetAsync($"/v1.0/items/{setup.First}/relationships?$top=1", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await reader.DeleteAsync($"/v1.0/items/{setup.First}/relationships/{typedEdge}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/v1.0/items/{setup.Second}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.PutAsync(setup.RelationUrl, null, Ct)).StatusCode);
        (await setup.Client.PutAsJsonAsync($"{targetUrl}/permissions/grants", new
        {
            grants = new[] { new { principalType = "user", principalId = userId, level = "read" } },
        }, Ct)).EnsureSuccessStatusCode();
        Assert.Single(await RelatedAsync(reader, setup.First));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync(setup.RelationUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/v1.0/items/{setup.First}/relationships/{typedEdge}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/v1.0/items/{setup.First}/relationships", new { otherId = setup.Second, type = "supports" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync(setup.RelationUrl, null, Ct)).StatusCode);
        var writable = await (await reader.GetAsync("/v1.0/items?writable=true", Ct)).ReadJsonAsync();
        Assert.DoesNotContain(writable.GetProperty("value").EnumerateArray(), i => i.GetProperty("item").GetProperty("id").GetGuid() == setup.Second);
        (await MoveAsync(setup)).EnsureSuccessStatusCode();
        // Destination inherited access takes effect immediately.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/v1.0/items/{setup.First}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Moving_a_document_preserves_file_versions_comments_and_task_links()
    {
        await factory.CreateTenantAsync("global-document-move");
        var client = await ApiClient.CreateAsync(factory, "global-document-move");
        var ws = await client.CreateWorkspaceAsync("Original documents");
        var destinationWs = await client.CreateWorkspaceAsync("Filed documents");
        async Task<Guid> LibraryAsync(Guid workspace) =>
            (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists", new { name = "Documents", templateKey = "documents" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var library = await LibraryAsync(ws);
        var destination = await LibraryAsync(destinationWs);
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n% preserved\n%%EOF\n");
        using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "record.pdf" } };
        var uploaded = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{library}/documents", form, Ct);
        uploaded.EnsureSuccessStatusCode();
        var id = (await uploaded.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        var sourceUrl = $"/v1.0/workspaces/{ws}/lists/{library}/items/{id}";
        (await client.PostAsJsonAsync($"{sourceUrl}/comments", new { text = "Keep this comment" }, Ct)).EnsureSuccessStatusCode();
        var tasks = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Tasks", templateKey = "tasks" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var taskResponse = await client.PostAsJsonAsync($"{sourceUrl}/tasks", new { workspaceId = ws, listId = tasks }, Ct);
        taskResponse.EnsureSuccessStatusCode();
        var taskId = (await taskResponse.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        var setup = new Setup(client, ws, library, destinationWs, destination, id, Guid.Empty);
        var moved = await MoveAsync(setup);
        Assert.True(moved.StatusCode == HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(Ct));
        Assert.Equal(bytes, await client.GetByteArrayAsync($"{setup.DestinationUrl}/file", Ct));
        var versions = await (await client.GetAsync($"{setup.DestinationUrl}/file/versions", Ct)).ReadJsonAsync();
        Assert.Single(versions.GetProperty("value").EnumerateArray());
        var comments = await (await client.GetAsync($"{setup.DestinationUrl}/comments", Ct)).ReadJsonAsync();
        Assert.Equal("Keep this comment", Assert.Single(comments.GetProperty("value").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{sourceUrl}/file", Ct)).StatusCode);
        var linkedTasks = await (await client.GetAsync($"{setup.DestinationUrl}/tasks", Ct)).ReadJsonAsync();
        Assert.Equal(taskId, Assert.Single(linkedTasks.GetProperty("value").EnumerateArray()).GetProperty("itemId").GetGuid());
        var links = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{tasks}/items/{taskId}/links", Ct)).ReadJsonAsync();
        var linkedDocument = Assert.Single(links.GetProperty("documents").EnumerateArray());
        Assert.Equal(destinationWs, linkedDocument.GetProperty("workspaceId").GetGuid());
        Assert.Equal(destination, linkedDocument.GetProperty("listId").GetGuid());
    }

    [Fact]
    public async Task A_failing_move_participant_rolls_back_item_and_module_location_changes()
    {
        await factory.CreateTenantAsync("global-move-rollback");
        var client = await ApiClient.CreateAsync(factory, "global-move-rollback");
        var ws = await client.CreateWorkspaceAsync("Rollback");
        async Task<Guid> LibraryAsync(string name) =>
            (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey = "documents" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var source = await LibraryAsync("Source");
        var destination = await LibraryAsync("Destination");
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n% rollback\n%%EOF\n");
        using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "rollback.pdf" } };
        var uploaded = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{source}/documents", form, Ct);
        uploaded.EnsureSuccessStatusCode();
        var id = (await uploaded.ReadJsonAsync()).GetProperty("itemId").GetGuid();
        var setup = new Setup(client, ws, source, ws, destination, id, Guid.Empty);
        Assert.Equal(HttpStatusCode.InternalServerError, (await MoveAsync(setup)).StatusCode);
        Assert.Equal(bytes, await client.GetByteArrayAsync($"{setup.SourceUrl}/file", Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(setup.DestinationUrl, Ct)).StatusCode);
        var located = await (await client.GetAsync($"/v1.0/items/{id}", Ct)).ReadJsonAsync();
        Assert.Equal(source, located.GetProperty("item").GetProperty("listId").GetGuid());
    }
}

/// <summary>Runs after module participants, so the test verifies their writes roll back too.</summary>
internal sealed class FailingMoveParticipant(ITenantContext tenant) : IItemMoveParticipant
{
    public Task MoveAsync(ItemMove move, DbTransaction transaction, CancellationToken cancellationToken) =>
        tenant.TenantIdentifier == "global-move-rollback" ? throw new InvalidOperationException("Test move failure.") : Task.CompletedTask;
}
