using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Search.Data;
using PaperDotNet.Search.Features;
using PaperDotNet.Tenancy.Contracts;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>Semantic search (SRC-07), hybrid ranking (SRC-08) and page-level hits (SRC-09), with the concept test model.</summary>
public sealed class SemanticSearchTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A PDF with one text line per page.</summary>
    private static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            builder.AddPage(PageSize.A4).AddText(text, 20, new PdfPoint(40, 760), font);
        }

        return builder.Build();
    }

    private static async Task<(Guid Workspace, Guid Library)> LibraryAsync(HttpClient client, string name)
    {
        var ws = await client.CreateWorkspaceAsync(name);
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        return (ws, (await response.ReadJsonAsync()).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid ws, Guid list, byte[] content, string fileName)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        var response = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", form, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("itemId").GetGuid();
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string query, string? mode = null)
    {
        var url = $"/v1.0/search?q={Uri.EscapeDataString(query)}" + (mode is null ? string.Empty : $"&mode={mode}");
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.ReadJsonAsync();
    }

    private static JsonElement? Hit(JsonElement result, Guid id) =>
        result.GetProperty("value").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(h => h!.Value.GetProperty("id").GetGuid() == id);

    /// <summary>Runs the embedding job of the tenant (normally every minute).</summary>
    private async Task EmbedAsync(TenantSummary tenant)
    {
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        await scope.ServiceProvider.GetRequiredService<EmbeddingJob>().RunAsync(Ct);
    }

    /// <summary>Waits until the item has passages in the index (indexing is asynchronous).</summary>
    private async Task IndexedAsync(TenantSummary tenant, Guid item, int passages)
    {
        await Eventually.WaitForAsync<bool>(async () =>
        {
            await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
            var db = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
            return await db.Passages.CountAsync(p => p.DocumentId == item && p.Page != null, Ct) >= passages ? true : null;
        }, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task Documents_are_found_by_meaning_on_the_matching_page()
    {
        var tenant = await factory.CreateTenantAsync("semantic-pages");
        var client = await ApiClient.CreateAsync(factory, "semantic-pages");
        var (ws, list) = await LibraryAsync(client, "Garage");
        var manual = await UploadAsync(client, ws, list, Pdf("Table of contents", "Automobile repair and brakes", "Warranty terms"), "manual.pdf");
        var other = await UploadAsync(client, ws, list, Pdf("Garden furniture catalogue"), "garden.pdf");
        await IndexedAsync(tenant, manual, 3);
        await IndexedAsync(tenant, other, 1);
        await EmbedAsync(tenant);

        // "car" is not in the text: keyword search misses it, semantic search finds page 2.
        Assert.Null(Hit(await SearchAsync(client, "car", "keyword"), manual));
        var semantic = await SearchAsync(client, "car", "semantic");
        Assert.Equal("semantic", semantic.GetProperty("mode").GetString());
        var hit = Hit(semantic, manual)!.Value;
        Assert.Equal(2, hit.GetProperty("page").GetInt32());
        Assert.Contains("Automobile repair", hit.GetProperty("snippet").GetString(), StringComparison.Ordinal);
        Assert.Equal(["semantic"], hit.GetProperty("matchedBy").EnumerateArray().Select(m => m.GetString()));
        Assert.Null(Hit(semantic, other));

        // Keyword hits point to their page too (SRC-09).
        var keyword = Hit(await SearchAsync(client, "warranty", "keyword"), manual)!.Value;
        Assert.Equal(3, keyword.GetProperty("page").GetInt32());

        // Hybrid: found both ways ranks first; exclusions apply to semantic matches as well.
        var hybrid = await SearchAsync(client, "automobile brakes", "hybrid");
        var first = hybrid.GetProperty("value")[0];
        Assert.Equal(manual, first.GetProperty("id").GetGuid());
        Assert.Equal(["keyword", "semantic"], first.GetProperty("matchedBy").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(2, first.GetProperty("page").GetInt32());
        Assert.Null(Hit(await SearchAsync(client, "vehicle -warranty", "hybrid"), manual));
    }

    [Fact]
    public async Task Semantic_search_is_trimmed_validated_and_does_not_embed_unchanged_text_again()
    {
        var tenant = await factory.CreateTenantAsync("semantic-acl");
        var admin = await ApiClient.CreateAsync(factory, "semantic-acl");
        var ws = await admin.CreateWorkspaceAsync("Clinic");
        var contentType = await admin.CreateContentTypeAsync("Note", [new { name = "text", type = "note" }]);
        var list = await admin.CreateListAsync(ws, "Notes", contentType);
        var note = (await admin.CreateItemAsync(ws, list, new { fields = new { title = "Visit", text = "The physician recommended rest" } })).GetProperty("id").GetGuid();
        await Eventually.WaitForAsync<bool>(async () =>
        {
            await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
            return await scope.ServiceProvider.GetRequiredService<SearchDbContext>().Passages.AnyAsync(p => p.DocumentId == note, Ct) ? true : null;
        });
        await EmbedAsync(tenant);
        Assert.NotNull(Hit(await SearchAsync(admin, "doctor", "semantic"), note));

        // Re-indexing unchanged text keeps the embeddings.
        var before = ConceptEmbeddingGenerator.Instance.Embedded;
        await using (var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            await scope.ServiceProvider.GetRequiredService<PaperDotNet.Lists.Contracts.IListItemStore>().ReindexAsync(note, Ct);
            await scope.ServiceProvider.GetRequiredService<EmbeddingJob>().RunAsync(Ct);
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<SearchDbContext>().Passages.CountAsync(p => p.EmbeddingModel == null, Ct));
        }

        Assert.Equal(before, ConceptEmbeddingGenerator.Instance.Embedded);

        // A user without access and another tenant see nothing.
        var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = "outsider", password = "outsider-password-1" }, Ct);
        Assert.True(created.IsSuccessStatusCode);
        var outsider = await ApiClient.CreateAsync(factory, "semantic-acl", "outsider", "outsider-password-1");
        Assert.Null(Hit(await SearchAsync(outsider, "doctor", "semantic"), note));
        await factory.CreateTenantAsync("semantic-acl-b");
        var foreign = await ApiClient.CreateAsync(factory, "semantic-acl-b");
        Assert.Null(Hit(await SearchAsync(foreign, "doctor", "semantic"), note));

        // Validation.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/v1.0/search?q=doctor&mode=magic", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/v1.0/search?workspaceId={ws}&mode=semantic", Ct)).StatusCode);
        Assert.Equal("keyword", (await SearchAsync(admin, "physician")).GetProperty("mode").GetString());
    }
}
