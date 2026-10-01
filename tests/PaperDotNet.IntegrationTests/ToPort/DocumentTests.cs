using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Documents.Features;
using PaperDotNet.Lists.Data;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Documents still to port: the inbox and Home libraries (T15c). The rest is in <c>DocumentTests</c>.</summary>
public sealed class DocumentTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Pdf(string marker) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {marker}\n%%EOF\n");

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static MultipartFormDataContent Form(byte[] content, string fileName, string? title = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        return form;
    }

    private static async Task<(Guid Workspace, Guid Library)> LibraryAsync(HttpClient client)
    {
        var ws = await client.CreateWorkspaceAsync("Records");
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (ws, (await response.ReadJsonAsync()).GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid ws, Guid list, byte[] content, string fileName, string? title = null) =>
        client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", Form(content, fileName, title), Ct);

    private static async Task<JsonElement> UploadOkAsync(HttpClient client, Guid ws, Guid list, byte[] content, string fileName)
    {
        var response = await UploadAsync(client, ws, list, content, fileName);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadJsonAsync();
    }

    [Fact]
    public async Task Uploads_to_the_inbox_land_in_the_home_workspace()
    {
        await factory.CreateTenantAsync("doc-inbox");
        var client = await ApiClient.CreateAsync(factory, "doc-inbox");

        var response = await client.PostAsync("/v1.0/me/inbox/documents", Form(Pdf("inbox"), "scan.pdf", "Scan from the copier"), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var document = await response.ReadJsonAsync();
        var home = await (await client.GetAsync("/v1.0/me/home", Ct)).ReadJsonAsync();

        Assert.Equal(home.GetProperty("inboxListId").GetGuid(), document.GetProperty("listId").GetGuid());
        Assert.Equal("Scan from the copier", document.GetProperty("fields").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Home_libraries_use_the_document_content_type()
    {
        var tenant = await factory.CreateTenantAsync("doc-home-type");
        var client = await ApiClient.CreateAsync(factory, "doc-home-type");
        var home = await (await client.GetAsync("/v1.0/me/home", Ct)).ReadJsonAsync();
        var ws = home.GetProperty("workspaceId").GetGuid();
        var library = home.GetProperty("documentsListId").GetGuid();

        var list = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{library}", Ct)).ReadJsonAsync();
        var contentType = list.GetProperty("contentTypes")[0];
        Assert.Equal("document", contentType.GetProperty("key").GetString());
        Assert.Contains(contentType.GetProperty("fields").EnumerateArray(), field => field.GetProperty("name").GetString() == "description");

        var created = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{library}/items", new { fields = new { title = "Scan", description = "From the copier" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal("From the copier", (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{library}/items/{item}", Ct)).ReadJsonAsync()).GetProperty("fields").GetProperty("description").GetString());

        // A library left on the generic Item type (older homes) is switched, and its items move with it.
        var plain = await client.CreateListAsync(ws, "Plain");
        var generic = (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{plain}", Ct)).ReadJsonAsync()).GetProperty("contentTypes")[0].GetProperty("id").GetGuid();
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
            var row = await db.Lists.FirstAsync(l => l.Id == library, Ct);
            row.TemplateKey = null;
            row.ContentTypeIds = [generic];
            (await db.Items.FirstAsync(i => i.Id == item, Ct)).ContentTypeId = generic;
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(library, (await (await client.GetAsync("/v1.0/me/home", Ct)).ReadJsonAsync()).GetProperty("documentsListId").GetGuid());
        var repaired = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{library}/items/{item}", Ct)).ReadJsonAsync();
        Assert.Equal(contentType.GetProperty("id").GetGuid(), repaired.GetProperty("contentTypeId").GetGuid());
        var etag = (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{library}/items/{item}", Ct)).Headers.ETag!.Tag;
        var updated = await client.SendWithEtagAsync(HttpMethod.Patch, $"/v1.0/workspaces/{ws}/lists/{library}/items/{item}", etag, new { fields = new { description = "Filed by the agent" } });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Filed by the agent", (await updated.ReadJsonAsync()).GetProperty("fields").GetProperty("description").GetString());
    }

    private sealed class ShiftedTime(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
