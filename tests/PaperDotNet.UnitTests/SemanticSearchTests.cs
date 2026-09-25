using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.AI;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Features;

namespace PaperDotNet.UnitTests;

/// <summary>Passages, query text, nearest neighbours and provider configuration of semantic search (SRC-07…09).</summary>
public sealed class SemanticSearchTests
{
    private static SearchDocumentData Document(string body, params string[] pages) =>
        new(Guid.NewGuid(), "listItem", Guid.NewGuid(), null, null, "Title", body, [], [], null, DateTimeOffset.UnixEpoch) { Pages = pages };

    [Fact]
    public void Documents_split_into_overlapping_passages_with_pages()
    {
        var words = string.Join(' ', Enumerable.Range(1, 600).Select(i => $"w{i}"));
        var passages = Passages.Split(Document("Fields  and\n tags", "Page one", words));

        Assert.Equal(new PassageText(null, "Fields and tags"), passages[0]);
        Assert.Equal(new PassageText(1, "Page one"), passages[1]);
        var long2 = passages.Where(p => p.Page == 2).ToList();
        Assert.True(long2.Count > 1);
        Assert.All(long2, p => Assert.True(p.Text.Length <= Passages.MaxChars));

        // Windows overlap and end at word boundaries.
        var firstEnd = long2[0].Text.Split(' ')[^1];
        Assert.Contains(firstEnd, long2[1].Text.Split(' '));
        Assert.All(long2, p => Assert.Matches(@"^w\d+( w\d+)*$", p.Text));

        // A document with no text still has its title as a passage.
        Assert.Equal([new PassageText(null, "Title")], Passages.Split(Document(string.Empty)));
    }

    [Theory]
    [InlineData("invoices from ACME", "invoices from ACME")]
    [InlineData("\"exact phrase\" -draft NOT old prefix* OR other", "exact phrase prefix other")]
    [InlineData("-only", "-only")]
    public void Queries_become_plain_text_for_embedding(string query, string expected) =>
        Assert.Equal(expected, SearchService.SemanticText(query));

    [Fact]
    public void Nearest_passages_are_filtered_by_similarity_and_scope()
    {
        static float[] Unit(params float[] v)
        {
            Vectors.Normalize(v);
            return v;
        }

        var ws = Guid.NewGuid();
        var close = new VectorEntry(Guid.NewGuid(), Guid.NewGuid(), ws, null, Unit(1, 0.1f));
        var far = new VectorEntry(Guid.NewGuid(), Guid.NewGuid(), ws, null, Unit(0, 1));
        var elsewhere = new VectorEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Unit(1, 0));
        var otherSize = new VectorEntry(Guid.NewGuid(), Guid.NewGuid(), ws, null, Unit(1, 0, 0));
        var snapshot = new VectorSnapshot("m", [close, far, elsewhere, otherSize], 4, 1);

        var matches = snapshot.Nearest(Unit(1, 0), 10, 0.5f, ws, null).ToList();

        Assert.Equal([close.PassageId], matches.Select(m => m.PassageId));
        Assert.Equal(2, snapshot.Nearest(Unit(1, 0), 2, -1, null, null).Count());
        Assert.Equal(new[] { 0.6f, 0.8f }, Vectors.FromBytes(Vectors.ToBytes([3, 4])));
    }

    [Fact]
    public void Embedding_providers_come_from_configuration()
    {
        static IServiceProvider Build(Dictionary<string, string?> settings) =>
            new ServiceCollection().AddLogging().AddPaperDotNetAI(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()).BuildServiceProvider();

        Assert.Null(Build([]).GetService<IEmbeddingGenerator<string, Embedding<float>>>());
        Assert.Throws<InvalidOperationException>(() => Build(new() { ["AI:Embeddings:Provider"] = "openai" }));
        Assert.Throws<InvalidOperationException>(() => Build(new() { ["AI:Embeddings:Provider"] = "magic" }));
        var generator = Build(new()
        {
            ["AI:Embeddings:Provider"] = "openai",
            ["AI:Embeddings:Endpoint"] = "http://localhost:11434/v1",
            ["AI:Embeddings:Model"] = "nomic-embed-text",
        }).GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        Assert.Equal("nomic-embed-text", generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId);
        Assert.StartsWith("openai:nomic-embed-text", SemanticSearch.ModelKeyOf(generator), StringComparison.Ordinal);
    }
}
