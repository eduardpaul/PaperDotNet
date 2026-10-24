using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PaperDotNet.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Data;

namespace PaperDotNet.IntegrationTests;

public sealed class ContentApiTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Root = "/v1.0/content";

    private async Task<(HttpClient Client, string Workspace, string Collection, string Type)> SetupAsync(string tenant, bool publishing = true, bool library = false)
    {
        await factory.CreateTenantAsync(tenant);
        var client = await ApiClient.CreateAsync(factory, tenant);
        var workspace = await PostAsync(client, Root + "/workspaces", new { name = "Records" });
        var workspaceId = workspace.GetProperty("id").GetString()!;
        var collection = await PostAsync(client, $"{Root}/workspaces/{workspaceId}/collections", new { name = "Contracts", library, publishingEnabled = publishing });
        var collectionId = collection.GetProperty("id").GetString()!;
        var current = await GetAsync(client, $"{Root}/collections/{collectionId}");
        var type = current.GetProperty("schema").GetProperty("content_types")[0].GetProperty("id").GetString()!;
        var schema = new EffectiveSchema([new SchemaField("reference", "Reference", "integer", Indexed: true), new SchemaField("status", "Status", "text")],
            [new SchemaContentType(type, "item", "Item", ["reference", "status"], true)]);
        (await client.SendWithEtagAsync(HttpMethod.Put, $"{Root}/collections/{collectionId}/schema", "\"1\"", schema)).EnsureSuccessStatusCode();
        return (client, workspaceId, collectionId, type);
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadJsonAsync();
    }
    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadJsonAsync();
    }
    private static string Etag(JsonElement item) => "\"" + item.GetProperty("resource").GetProperty("version").GetInt64() + "\"";
    private static object Input(string type, string status) => new { name = "Contract", contentTypeId = type, values = new { reference = "9223372036854775807", status } };

    [Fact]
    public async Task Drafts_publish_frozen_content_and_preserve_numeric_precision_and_archive_revisions()
    {
        var (client, _, collection, type) = await SetupAsync("content-lifecycle");
        var created = await PostAsync(client, $"{Root}/resources/{collection}/items", new { content = Input(type, "draft") });
        var id = created.GetProperty("resource").GetProperty("id").GetString();
        Assert.Equal("9223372036854775807", created.GetProperty("values").GetProperty("reference").GetString());
        Assert.Empty((await GetAsync(client, $"{Root}/collections/{collection}/items")).EnumerateArray());
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.PostAsync($"{Root}/items/{id}/publish", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendWithEtagAsync(HttpMethod.Post, $"{Root}/items/{id}/publish", "\"99\"")).StatusCode);
        (await client.SendWithEtagAsync(HttpMethod.Post, $"{Root}/items/{id}/publish", Etag(created))).EnsureSuccessStatusCode();
        var published = await GetAsync(client, $"{Root}/items/{id}");
        var edit = await client.SendWithEtagAsync(HttpMethod.Put, $"{Root}/items/{id}", Etag(published), Input(type, "changed"));
        edit.EnsureSuccessStatusCode();
        var draft = await edit.ReadJsonAsync();
        Assert.Equal("draft", (await GetAsync(client, $"{Root}/items/{id}")).GetProperty("values").GetProperty("status").GetString());
        Assert.Equal("changed", (await GetAsync(client, $"{Root}/items/{id}?surface=head")).GetProperty("values").GetProperty("status").GetString());
        var archived = await client.SendWithEtagAsync(HttpMethod.Put, $"{Root}/items/{id}/archive", Etag(draft), new { archived = true });
        archived.EnsureSuccessStatusCode();
        var after = await GetAsync(client, $"{Root}/items/{id}?surface=head");
        Assert.Equal(draft.GetProperty("revisionId").GetString(), after.GetProperty("revisionId").GetString());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("resource").GetProperty("archivedAt").ValueKind);
        Assert.Empty((await GetAsync(client, $"{Root}/collections/{collection}/items")).EnumerateArray());
        Assert.Single((await GetAsync(client, $"{Root}/collections/{collection}/items?archive=archived")).EnumerateArray());
        Assert.Contains((await GetAsync(client, $"{Root}/items?archive=archived")).EnumerateArray(), x => x.GetProperty("resource").GetProperty("id").GetString() == id);
        (await client.SendWithEtagAsync(HttpMethod.Put, $"{Root}/items/{id}/archive", Etag(after), new { archived = false })).EnsureSuccessStatusCode();
        Assert.Single((await GetAsync(client, $"{Root}/collections/{collection}/items")).EnumerateArray());
    }

    [Fact]
    public async Task Draft_and_publish_grants_are_independent_and_tenants_are_isolated()
    {
        var (admin, workspace, collection, type) = await SetupAsync("content-permissions");
        var user = await PostAsync(admin, "/v1.0/users", new { userName = "reader", password = "reader-password-1" });
        var userId = user.GetProperty("id").GetString()!;
        var me = await GetAsync(admin, "/v1.0/me");
        var owner = me.GetProperty("id").GetString()!;
        var grants = new[] { new ContentGrant("user", owner, "manage"), new ContentGrant("user", userId, "read") };
        (await admin.SendWithEtagAsync(HttpMethod.Put, $"{Root}/resources/{workspace}/grants", "\"1\"", new { grants })).EnsureSuccessStatusCode();
        var created = await PostAsync(admin, $"{Root}/resources/{collection}/items", new { content = Input(type, "private") });
        var id = created.GetProperty("resource").GetProperty("id").GetString();
        using var reader = await ApiClient.CreateAsync(factory, "content-permissions", "reader", "reader-password-1");
        Assert.Empty((await GetAsync(reader, $"{Root}/collections/{collection}/items")).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Root}/items/{id}?surface=head", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendWithEtagAsync(HttpMethod.Post, $"{Root}/items/{id}/publish", Etag(created))).StatusCode);
        (await admin.SendWithEtagAsync(HttpMethod.Post, $"{Root}/items/{id}/publish", Etag(created))).EnsureSuccessStatusCode();
        Assert.Equal("private", (await GetAsync(reader, $"{Root}/items/{id}")).GetProperty("values").GetProperty("status").GetString());
        await factory.CreateTenantAsync("content-other");
        using var other = await ApiClient.CreateAsync(factory, "content-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Root}/items/{id}", Ct)).StatusCode);
        Assert.Empty((await GetAsync(other, Root + "/workspaces")).EnumerateArray());
    }

    [Fact]
    public async Task Migration_preserves_ids_and_history_rejects_stale_preview_and_freezes_source_writes()
    {
        var tenant = await factory.CreateTenantAsync("content-import");
        using var admin = await ApiClient.CreateAsync(factory, "content-import");
        var workspace = await admin.CreateWorkspaceAsync("Import me");
        var type = await admin.CreateContentTypeAsync("Record", [new { name = "status", type = "text" }]);
        var list = await PostAsync(admin, $"/v1.0/workspaces/{workspace}/lists", new { name = "Records", contentTypeIds = new[] { type }, versioning = "major" });
        var listId = list.GetProperty("id").GetGuid();
        var item = await admin.CreateItemAsync(workspace, listId, new { fields = new { title = "Original", status = "one" } });
        var itemId = item.GetProperty("id").GetGuid();
        var path = $"/v1.0/workspaces/{workspace}/lists/{listId}/items/{itemId}";
        var preview = await GetAsync(admin, $"{Root}/migrations/{workspace}/preview");
        var etag = (await admin.GetAsync(path, Ct)).Headers.ETag!.Tag;
        (await admin.SendWithEtagAsync(HttpMethod.Patch, path, etag, new { fields = new { status = "two" } })).EnsureSuccessStatusCode();
        var stale = await admin.PostAsJsonAsync($"{Root}/migrations/{workspace}", new { sourceHash = preview.GetProperty("sourceHash").GetString() }, Ct);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        preview = await GetAsync(admin, $"{Root}/migrations/{workspace}/preview");
        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
        await PostAsync(admin, $"{Root}/migrations/{workspace}", new { sourceHash = preview.GetProperty("sourceHash").GetString() });
        var imported = await GetAsync(admin, $"{Root}/items/{itemId}");
        Assert.Equal(itemId.ToString(), imported.GetProperty("resource").GetProperty("id").GetString());
        Assert.Equal("two", imported.GetProperty("values").GetProperty("status").GetString());
        Assert.NotEmpty((await GetAsync(admin, $"{Root}/items/{itemId}/history")).EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.GetAsync(path, Ct)).StatusCode);
        var blocked = await admin.SendWithEtagAsync(HttpMethod.Patch, path, etag, new { fields = new { status = "diverged" } });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
            var source = await db.Items.SingleAsync(x => x.Id == itemId, Ct);
            source.Title = "Background divergence";
            var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("moved to Content", exception.Message, StringComparison.Ordinal);
        }
        var edit = await admin.SendWithEtagAsync(HttpMethod.Put, $"{Root}/items/{itemId}", Etag(imported), new { name = "Imported", contentTypeId = type, values = new { status = "canonical" } });
        edit.EnsureSuccessStatusCode();
        Assert.Equal("canonical", (await GetAsync(admin, $"{Root}/items/{itemId}")).GetProperty("values").GetProperty("status").GetString());
    }
    [Fact]
    public async Task Published_files_remain_pinned_when_a_replacement_is_saved_as_a_draft()
    {
        var (client, _, collection, type) = await SetupAsync("content-files", library: true);
        var created = await PostAsync(client, $"{Root}/resources/{collection}/items", new { content = Input(type, "document") });
        var id = created.GetProperty("resource").GetProperty("id").GetString();
        var original = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n% original\n%%EOF");
        var replacement = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n% replacement\n%%EOF");
        async Task<HttpResponseMessage> Upload(byte[] bytes, string etag)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{Root}/items/{id}/file");
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            request.Content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "contract.pdf" } };
            return await client.SendAsync(request, Ct);
        }
        var first = await Upload(original, Etag(created)); first.EnsureSuccessStatusCode();
        var uploaded = await first.ReadJsonAsync();
        (await client.SendWithEtagAsync(HttpMethod.Post, $"{Root}/items/{id}/publish", Etag(uploaded))).EnsureSuccessStatusCode();
        var published = await GetAsync(client, $"{Root}/items/{id}");
        var second = await Upload(replacement, Etag(published)); second.EnsureSuccessStatusCode();
        Assert.Equal(original, await client.GetByteArrayAsync($"{Root}/items/{id}/file", Ct));
        Assert.Equal(replacement, await client.GetByteArrayAsync($"{Root}/items/{id}/file?surface=head", Ct));
        var head = await second.ReadJsonAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload([1, 2, 3, 4], Etag(head))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Upload(original, Etag(created))).StatusCode);
        Assert.Equal(head.GetProperty("revisionId").GetString(), (await GetAsync(client, $"{Root}/items/{id}?surface=head")).GetProperty("revisionId").GetString());
    }

}
