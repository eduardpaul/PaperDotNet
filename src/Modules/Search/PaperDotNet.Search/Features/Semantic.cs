using System.Collections.Concurrent;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Search.Data;

namespace PaperDotNet.Search.Features;

/// <summary>Options of search (<c>Search</c> section).</summary>
public sealed class SearchOptions
{
    public const string Section = "Search";

    /// <summary>Mode when a request names none (<c>keyword</c>, <c>semantic</c>, <c>hybrid</c>): hybrid when embeddings are configured, else keyword.</summary>
    public string? DefaultMode { get; set; }

    /// <summary>Passages less similar than this (cosine, -1 to 1) are not semantic matches. Depends on the model.</summary>
    public float MinSimilarity { get; set; } = 0.3f;

    /// <summary>Documents taken from each side before hybrid fusion (and at most returned by semantic search).</summary>
    public int CandidateLimit { get; set; } = 200;

    /// <summary>Passages sent to the embedding model per call.</summary>
    public int EmbeddingBatchSize { get; set; } = 64;

    /// <summary>Passages embedded per tenant and run of the embedding job.</summary>
    public int EmbeddingsPerRun { get; set; } = 2000;

    /// <summary>Cron schedule of the embedding job (UTC).</summary>
    public string EmbeddingSchedule { get; set; } = "* * * * *";
}

/// <summary>How results are found (SRC-08), as in <c>mode</c>.</summary>
internal static class SearchModes
{
    /// <summary>Full-text search only.</summary>
    public const string Keyword = "keyword";

    /// <summary>Embeddings only: documents by meaning.</summary>
    public const string Semantic = "semantic";

    /// <summary>Both, fused with reciprocal rank fusion.</summary>
    public const string Hybrid = "hybrid";

    /// <summary>The mode named by <paramref name="value"/> (any case), or null.</summary>
    public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Keyword => Keyword,
        Semantic => Semantic,
        Hybrid => Hybrid,
        _ => null,
    };
}

/// <summary>A semantic match: the document and its most similar passage.</summary>
internal sealed record SemanticMatch(Guid DocumentId, Guid PassageId, float Similarity);

/// <summary>
/// Semantic search (SRC-07, ADR-0027): embeds the query with the configured <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>
/// and finds the most similar passages in the tenant's in-memory <see cref="VectorIndex"/>. Security trimming and filters
/// are applied afterwards in SQL by the caller. Available only when an embedding provider is configured.
/// </summary>
internal sealed class SemanticSearch(
    SearchDbContext db, VectorIndex vectors, IMemoryCache cache, IOptions<SearchOptions> options, IEmbeddingGenerator<string, Embedding<float>>? generator)
{
    /// <summary>Passages looked at per query; several may belong to one document.</summary>
    private const int PassageCandidates = 1000;

    public IEmbeddingGenerator<string, Embedding<float>>? Generator => generator;

    public bool Enabled => generator is not null;

    /// <summary>Identifies the model (and vector size) embeddings come from; stored with each embedding.</summary>
    public string ModelKey => ModelKeyOf(generator!);

    public static string ModelKeyOf(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        var key = $"{metadata?.ProviderName}:{metadata?.DefaultModelId}:{metadata?.DefaultModelDimensions}";
        return key.Length > 200 ? key[..200] : key;
    }

    /// <summary>The documents of the tenant most similar to <paramref name="text"/>, best first, optionally only in a workspace or container.</summary>
    public async Task<List<SemanticMatch>> SearchAsync(Guid tenantId, string text, Guid? workspaceId, Guid? containerId, CancellationToken ct)
    {
        var model = ModelKey;
        var key = $"search:query:{model}:{Passages.Hash(text)}";
        if (!cache.TryGetValue(key, out float[]? query) || query is null)
        {
            query = (await generator!.GenerateAsync([text], cancellationToken: ct))[0].Vector.ToArray();
            Vectors.Normalize(query);
            cache.Set(key, query, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(10) });
        }

        var snapshot = await vectors.GetAsync(db, tenantId, model, ct);
        var best = new Dictionary<Guid, SemanticMatch>();
        foreach (var match in snapshot.Nearest(query, PassageCandidates, options.Value.MinSimilarity, workspaceId, containerId))
        {
            if (!best.TryGetValue(match.DocumentId, out var current) || current.Similarity < match.Similarity)
            {
                best[match.DocumentId] = match;
            }
        }

        return [.. best.Values.OrderByDescending(m => m.Similarity).ThenBy(m => m.DocumentId)];
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

/// <summary>A stored embedding with where its document is.</summary>
internal sealed record VectorRow(Guid PassageId, Guid DocumentId, Guid WorkspaceId, Guid? ContainerId, byte[]? Embedding);

/// <summary>
/// The embeddings of each tenant in memory (ADR-0027), shared by all requests of this server. A read first checks the
/// database for changes (count and newest stamp of the model's embeddings) and loads only what changed, so every
/// server stays current without messages. Unused tenants are dropped after 30 minutes.
/// </summary>
internal sealed class VectorIndex(IMemoryCache cache)
{
    /// <summary>Embeddings stored within this window (ms) before the newest known one are read again (clock skew between servers).</summary>
    private const long SkewMilliseconds = 30_000;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<VectorSnapshot> GetAsync(SearchDbContext db, Guid tenantId, string model, CancellationToken ct)
    {
        var key = $"search:vectors:{tenantId:N}";
        var gate = _locks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var current = cache.Get<VectorSnapshot>(key);
            current = current?.Model == model ? current : VectorSnapshot.Empty(model);
            var database = db;
            var tenant = tenantId;
            var name = model;
            var token = ct;
            var count = await database.Passages.AsNoTracking()
                .CountAsync(p => p.TenantId == tenant && p.EmbeddingModel == name && p.Embedding != null, token);
            var newest = await database.Passages.AsNoTracking()
                .Where(p => p.TenantId == tenant && p.EmbeddingModel == name && p.Embedding != null)
                .MaxAsync(p => (long?)p.VectorStamp, token) ?? 0;
            if (count != current.Count || newest != current.Newest)
            {
                current = await RefreshAsync(db, tenantId, current, count, newest, ct);
            }

            cache.Set(key, current, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<VectorSnapshot> RefreshAsync(SearchDbContext db, Guid tenantId, VectorSnapshot current, int count, long newest, CancellationToken ct)
    {
        var entries = current.Entries.ToDictionary(e => e.PassageId);
        var database = db;
        var tenant = tenantId;
        var model = current.Model;
        var since = current.Newest - SkewMilliseconds;
        var token = ct;
        var changed = await database.Passages.AsNoTracking()
            .Where(p => p.TenantId == tenant && p.EmbeddingModel == model && p.Embedding != null && p.VectorStamp > since)
            .Join(
                database.Documents.AsNoTracking().Where(d => d.TenantId == tenant),
                p => p.DocumentId,
                d => d.Id,
                (p, d) => new VectorRow(p.Id, p.DocumentId, d.WorkspaceId, d.ContainerId, p.Embedding))
            .ToListAsync(token);
        foreach (var row in changed)
        {
            entries[row.PassageId] = new VectorEntry(row.PassageId, row.DocumentId, row.WorkspaceId, row.ContainerId, Vectors.FromBytes(row.Embedding!));
        }

        if (entries.Count != count)
        {
            // Passages were deleted (or documents are gone): keep only what still exists.
            var ids = (await database.Passages.AsNoTracking()
                .Where(p => p.TenantId == tenant && p.EmbeddingModel == model && p.Embedding != null)
                .Select(p => p.Id)
                .ToListAsync(token)).ToHashSet();
            foreach (var id in entries.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                entries.Remove(id);
            }
        }

        return new VectorSnapshot(current.Model, [.. entries.Values], count, newest);
    }
}

internal sealed record VectorEntry(Guid PassageId, Guid DocumentId, Guid WorkspaceId, Guid? ContainerId, float[] Vector);

/// <summary>An immutable set of embeddings; searches read it without locks.</summary>
internal sealed record VectorSnapshot(string Model, VectorEntry[] Entries, int Count, long Newest)
{
    public static VectorSnapshot Empty(string model) => new(model, [], -1, -1);

    /// <summary>The <paramref name="limit"/> passages most similar to the normalized <paramref name="query"/> (brute force).</summary>
    public IEnumerable<SemanticMatch> Nearest(float[] query, int limit, float minSimilarity, Guid? workspaceId, Guid? containerId)
    {
        var top = new PriorityQueue<SemanticMatch, float>();
        foreach (var entry in Entries)
        {
            if (entry.Vector.Length != query.Length
                || (workspaceId is { } ws && entry.WorkspaceId != ws)
                || (containerId is { } container && entry.ContainerId != container))
            {
                continue;
            }

            var similarity = TensorPrimitives.Dot(entry.Vector, query);
            if (similarity < minSimilarity)
            {
                continue;
            }

            if (top.Count < limit)
            {
                top.Enqueue(new SemanticMatch(entry.DocumentId, entry.PassageId, similarity), similarity);
            }
            else if (top.TryPeek(out _, out var lowest) && similarity > lowest)
            {
                top.DequeueEnqueue(new SemanticMatch(entry.DocumentId, entry.PassageId, similarity), similarity);
            }
        }

        return top.UnorderedItems.Select(i => i.Element);
    }
}

/// <summary>A passage to embed with its document's title.</summary>
internal sealed record PendingPassage(long Key, string Title, string Text);

/// <summary>
/// Embeds passages that have no embedding of the configured model yet (SRC-07): new or changed text, or all passages
/// after the model changed. Runs per tenant on <see cref="SearchOptions.EmbeddingSchedule"/>; when the provider fails
/// it stops and tries again next time.
/// </summary>
internal sealed partial class EmbeddingJob(
    SearchDbContext db, SemanticSearch semantic, IOptions<SearchOptions> options, TimeProvider time, ILogger<EmbeddingJob> logger) : ITenantRecurringJob
{
    public const string Name = "search.embeddings";

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!semantic.Enabled)
        {
            return;
        }

        var model = semantic.ModelKey;
        var batchSize = Math.Clamp(options.Value.EmbeddingBatchSize, 1, 2048);
        var database = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        for (var done = 0; done < options.Value.EmbeddingsPerRun;)
        {
            var size = batchSize;
            var batch = await database.Passages.AsNoTracking()
                .Where(p => p.TenantId == tenant && p.EmbeddingModel != model)
                .OrderBy(p => p.Key)
                .Select(p => new PendingPassage(
                    p.Key,
                    database.Documents.Where(d => d.TenantId == tenant && d.Id == p.DocumentId).Select(d => d.Title).FirstOrDefault() ?? "",
                    p.Text))
                .Take(size)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                return;
            }

            GeneratedEmbeddings<Embedding<float>> embeddings;
            try
            {
                embeddings = await semantic.Generator!.GenerateAsync(batch.Select(b => Passages.EmbeddingInput(b.Title, b.Text)), cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Any provider failure (network, quota, bad response): try again on the next run.
                LogEmbeddingFailed(ex, model);
                return;
            }

            var stamp = time.GetUtcNow().ToUnixTimeMilliseconds();
            var keys = batch.Select(b => b.Key).ToList();
            var first = keys[0];
            var last = keys[^1];
            var passages = (await database.Passages
                .Where(p => p.TenantId == tenant && p.Key >= first && p.Key <= last)
                .ToListAsync(ct)).ToDictionary(p => p.Key);
            foreach (var (item, embedding) in batch.Zip(embeddings))
            {
                if (passages.TryGetValue(item.Key, out var passage))
                {
                    passage.Embedding = Vectors.ToBytes(embedding.Vector.Span);
                    passage.EmbeddingModel = model;
                    passage.VectorStamp = stamp;
                }
            }

            await database.SaveChangesAsync(ct);
            database.ChangeTracker.Clear();
            done += batch.Count;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding passages with {Model} failed; retrying on the next run.")]
    private partial void LogEmbeddingFailed(Exception exception, string model);
}
