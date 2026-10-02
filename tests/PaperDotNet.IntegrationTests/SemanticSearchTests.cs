using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Search.Features;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>Semantic search (SRC-07), hybrid ranking (SRC-08) and page-level hits (SRC-09), with the concept test model.</summary>
public sealed class SemanticSearchTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ConceptEmbeddingGenerator _model = new();
    private readonly TestHost _host;
    private HttpClient _admin = null!;
    private Guid _tenant;

    public SemanticSearchTests() =>
        _host = new TestHost(services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_model));

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _tenant = await TenantOfAsync(_admin);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static async Task<Guid> TenantOfAsync(HttpClient client) =>
        Guid.Parse((await (await client.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("tenantId").GetString()!);

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

    private static async Task<(string Workspace, string Library)> LibraryAsync(HttpClient client, string name)
    {
        var ws = await Api.CreateWorkspaceAsync(client, name);
        using var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        return (ws, (await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private static async Task<Guid> UploadAsync(HttpClient client, string ws, string list, byte[] content, string fileName)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        using var response = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", form, Ct);
        return Guid.Parse((await response.JsonAsync(HttpStatusCode.Created)).GetProperty("itemId").GetString()!);
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string query, string? mode = null)
    {
        var url = $"/v1.0/search?q={Uri.EscapeDataString(query)}" + (mode is null ? string.Empty : $"&mode={mode}");
        using var response = await client.GetAsync(url, Ct);
        return await response.JsonAsync(HttpStatusCode.OK);
    }

    private static JsonElement? Hit(JsonElement result, Guid id) =>
        result.GetProperty("value").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(h => h!.Value.GetProperty("id").GetGuid() == id);

    /// <summary>Runs the embedding job of the tenant (normally every minute).</summary>
    private async Task EmbedAsync(Guid tenant)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EmbeddingJob>().RunAsync(tenant, Ct);
    }

    /// <summary>Waits until the document has passages in the index (indexing is asynchronous).</summary>
    private async Task IndexedAsync(Guid tenant, Guid document, int passages, bool onPages = true)
    {
        for (var i = 0; i < 300; i++)
        {
            await using (var scope = _host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
                var count = await db.Passages.CountAsync(p => p.TenantId == tenant && p.DocumentId == document && (!onPages || p.Page != null), Ct);
                if (count >= passages)
                {
                    return;
                }
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail($"The document {document} was not indexed.");
    }

    [Fact]
    public async Task Documents_are_found_by_meaning_on_the_matching_page()
    {
        var (ws, list) = await LibraryAsync(_admin, "Garage");
        var manual = await UploadAsync(_admin, ws, list, Pdf("Table of contents", "Automobile repair and brakes", "Warranty terms"), "manual.pdf");
        var other = await UploadAsync(_admin, ws, list, Pdf("Garden furniture catalogue"), "garden.pdf");
        await IndexedAsync(_tenant, manual, 3);
        await IndexedAsync(_tenant, other, 1);
        await EmbedAsync(_tenant);

        // "car" is not in the text: keyword search misses it, semantic search finds page 2.
        Assert.Null(Hit(await SearchAsync(_admin, "car", "keyword"), manual));
        var semantic = await SearchAsync(_admin, "car", "semantic");
        Assert.Equal("semantic", semantic.GetProperty("mode").GetString());
        var hit = Hit(semantic, manual)!.Value;
        Assert.Equal(2, hit.GetProperty("page").GetInt32());
        Assert.Contains("Automobile repair", hit.GetProperty("snippet").GetString(), StringComparison.Ordinal);
        Assert.Equal(["semantic"], hit.GetProperty("matchedBy").EnumerateArray().Select(m => m.GetString()));
        Assert.Null(Hit(semantic, other));
        Assert.Equal(1, semantic.GetProperty("facets").GetProperty("workspace").EnumerateArray().Single().GetProperty("count").GetInt32());

        // Keyword hits point to their page too (SRC-09).
        var keyword = Hit(await SearchAsync(_admin, "warranty", "keyword"), manual)!.Value;
        Assert.Equal(3, keyword.GetProperty("page").GetInt32());

        // Hybrid (the default with embeddings): found both ways ranks first; exclusions apply to semantic matches as well.
        var hybrid = await SearchAsync(_admin, "automobile brakes");
        Assert.Equal("hybrid", hybrid.GetProperty("mode").GetString());
        var first = hybrid.GetProperty("value")[0];
        Assert.Equal(manual, first.GetProperty("id").GetGuid());
        Assert.Equal(["keyword", "semantic"], first.GetProperty("matchedBy").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(2, first.GetProperty("page").GetInt32());
        Assert.Null(Hit(await SearchAsync(_admin, "vehicle -warranty", "hybrid"), manual));

        // The MCP tool searches the same way.
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_admin.BaseAddress!, "/v1.0/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            _admin,
            ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        var result = await mcp.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "car", ["mode"] = "semantic" }, cancellationToken: Ct);
        var tool = JsonElement.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal("semantic", tool.GetProperty("mode").GetString());
        Assert.Equal(manual, tool.GetProperty("hits")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Semantic_search_is_trimmed_validated_and_does_not_embed_unchanged_text_again()
    {
        var ws = await Api.CreateWorkspaceAsync(_admin, "Clinic");
        var list = (await Api.CreateListAsync(_admin, ws, "Notes", new object[] { new { name = "text", displayName = "Text", type = "note" } })).Id();
        var note = Guid.Parse((await Api.CreateItemAsync(_admin, ws, list, new { title = "Visit", text = "The physician recommended rest" })).Id());
        await IndexedAsync(_tenant, note, 1, onPages: false);
        await EmbedAsync(_tenant);
        Assert.NotNull(Hit(await SearchAsync(_admin, "doctor", "semantic"), note));

        // Re-indexing unchanged text keeps the embeddings.
        var before = _model.Embedded;
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IListItemStore>().AsSystem(new ChangeActor(_tenant, null));
            await store.ReindexAsync(note, Ct);
            await scope.ServiceProvider.GetRequiredService<EmbeddingJob>().RunAsync(_tenant, Ct);
            var tenant = _tenant;
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<SearchDbContext>().Passages.CountAsync(p => p.TenantId == tenant && p.EmbeddingModel == null, Ct));
        }

        Assert.Equal(before, _model.Embedded);

        // A user without access and another tenant see nothing.
        using (var created = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = "outsider", password = "Outsider-Pass-1" }, Ct))
        {
            await created.JsonAsync(HttpStatusCode.Created);
        }

        var outsider = await _host.SignInAsync("outsider", "Outsider-Pass-1");
        Assert.Null(Hit(await SearchAsync(outsider, "doctor", "semantic"), note));
        var foreign = await _host.CreateTenantAsync("semantic-b");
        await EmbedAsync(await TenantOfAsync(foreign));
        Assert.Null(Hit(await SearchAsync(foreign, "doctor", "semantic"), note));

        // Validation.
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/v1.0/search?q=doctor&mode=magic", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync($"/v1.0/search?workspaceId={ws}&mode=semantic", Ct)).StatusCode);
        Assert.Equal("keyword", (await SearchAsync(_admin, "physician", "keyword")).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Without_embeddings_semantic_search_is_refused_and_keyword_is_the_default()
    {
        await using var host = new TestHost();
        var admin = await host.SignInAsync();
        using var refused = await admin.GetAsync("/v1.0/search?q=car&mode=semantic", Ct);
        Assert.Contains("AI:Embeddings", (await refused.JsonAsync(HttpStatusCode.BadRequest)).ToString(), StringComparison.Ordinal);
        Assert.Equal("keyword", (await SearchAsync(admin, "car")).GetProperty("mode").GetString());
    }
}
