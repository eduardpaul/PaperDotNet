using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using PaperDotNet.Search.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>Options of search (<c>Search</c> section).</summary>
public sealed class SearchOptions
{
    public const string Section = "Search";

    /// <summary>The search store (ADR-0043): <c>database</c> (default) or the name of an installed store.</summary>
    public string Store { get; set; } = "database";

    /// <summary>Mode when a request names none: <c>hybrid</c> when embeddings are configured, else <c>keyword</c>.</summary>
    public SearchMode? DefaultMode { get; set; }

    /// <summary>Passages less similar than this (cosine, -1 to 1) are not semantic matches. Depends on the model.</summary>
    public float MinSimilarity { get; set; } = 0.3f;

    /// <summary>Documents taken from each side before hybrid fusion (and at most returned by semantic search).</summary>
    public int CandidateLimit { get; set; } = 200;

    /// <summary>Passages sent to the embedding model per call.</summary>
    public int EmbeddingBatchSize { get; set; } = 64;


}

/// <summary>
/// The configured embedding model (SRC-07, ADR-0027): the configured <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>,
/// the key stored with each embedding, and query vectors (cached). Semantic search is available only when it is
/// configured; where vectors are stored and searched is up to the <see cref="ISearchStore"/>.
/// </summary>
internal sealed class EmbeddingModel(HybridCache cache, IServiceProvider services) : ISearchVectorSpace
{
    private int _dimensions;

    public IEmbeddingGenerator<string, Embedding<float>>? Generator { get; } = services.GetService(typeof(IEmbeddingGenerator<string, Embedding<float>>)) as IEmbeddingGenerator<string, Embedding<float>>;

    public bool Enabled => Generator is not null;

    /// <summary>Identifies the model (and vector size) embeddings come from; stored with each embedding.</summary>
    public string ModelKey => ModelKeyOf(Generator!);

    public static string ModelKeyOf(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        var key = $"{metadata?.ProviderName}:{metadata?.DefaultModelId}:{metadata?.DefaultModelDimensions}";
        return key.Length > 200 ? key[..200] : key;
    }

    /// <summary>The model and its vector length (from its metadata, else measured once with a short text).</summary>
    public async ValueTask<SearchVectorModel?> GetAsync(CancellationToken cancellationToken)
    {
        if (Generator is null)
        {
            return null;
        }

        if (_dimensions == 0)
        {
            var dimensions = Generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelDimensions
                ?? (await Generator.GenerateAsync(["dimensions"], cancellationToken: cancellationToken))[0].Vector.Length;
            Interlocked.CompareExchange(ref _dimensions, dimensions, 0);
        }

        return new SearchVectorModel(ModelKey, _dimensions);
    }

    /// <summary>The normalized embedding of a query.</summary>
    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct)
    {
        var model = ModelKey;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var vector = await cache.GetOrCreateAsync(
            $"search:query:{model}:{hash}",
            async token => (await Generator!.GenerateAsync([text], cancellationToken: token))[0].Vector.ToArray(),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30) },
            cancellationToken: ct);
        var copy = vector.ToArray();
        Vectors.Normalize(copy);
        return copy;
    }
}

/// <summary>Converting embeddings to and from their stored form (normalized little-endian float32).</summary>
internal static class Vectors
{
    public static void Normalize(Span<float> vector)
    {
        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0)
        {
            TensorPrimitives.Divide(vector, norm, vector);
        }
    }

    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var copy = vector.ToArray();
        Normalize(copy);
        return BitConverter.IsLittleEndian ? MemoryMarshal.AsBytes(copy.AsSpan()).ToArray() : throw new PlatformNotSupportedException("Big-endian platforms are not supported.");
    }

    public static float[] FromBytes(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}
