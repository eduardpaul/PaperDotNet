using System.Collections.Concurrent;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Search.Data;

namespace PaperDotNet.Search.Features;

/// <summary>Options of search (<c>Search</c> section).</summary>
public sealed class SearchOptions
{
    public const string Section = "Search";

    /// <summary>Mode when a request names none: <c>hybrid</c> when embeddings are configured, else <c>keyword</c>.</summary>
    public SearchMode? DefaultMode { get; set; }

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

/// <summary>How results are found (SRC-08).</summary>
public enum SearchMode
{
    /// <summary>Full-text search only.</summary>
    Keyword,

    /// <summary>Embeddings only: documents by meaning.</summary>
    Semantic,

    /// <summary>Both, fused with reciprocal rank fusion.</summary>
    Hybrid,
}

/// <summary>A semantic match: the document and its most similar passage.</summary>
internal sealed record SemanticMatch(Guid DocumentId, Guid PassageId, float Similarity);

/// <summary>
/// Semantic search (SRC-07, ADR-0027): embeds the query with the configured <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>
/// and finds the most similar passages in the tenant's in-memory <see cref="VectorIndex"/>. Security trimming and filters
/// are applied afterwards in SQL by the caller. Available only when an embedding provider is configured.
/// </summary>
internal sealed class SemanticSearch(
    SearchDbContext db, VectorIndex vectors, HybridCache cache, IOptions<SearchOptions> options, ITenantContext tenant, IServiceProvider services)
{
    /// <summary>Passages looked at per query; several may belong to one document.</summary>
    private const int PassageCandidates = 1000;

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

    /// <summary>The documents most similar to <paramref name="text"/>, best first, optionally only in a workspace or container.</summary>
    public async Task<List<SemanticMatch>> SearchAsync(string text, Guid? workspaceId, Guid? containerId, CancellationToken ct)
    {
        var model = ModelKey;
        var query = await cache.GetOrCreateAsync(
            $"search:query:{model}:{Passages.Hash(text)}",
            async token => (await Generator!.GenerateAsync([text], cancellationToken: token))[0].Vector.ToArray(),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30) },
            cancellationToken: ct);
        Vectors.Normalize(query);
        var snapshot = await vectors.GetAsync(db, tenant.TenantId!.Value, model, ct);
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

/// <summary>
/// The embeddings of a tenant in memory (ADR-0027), shared by all requests of this server. A read first checks the
/// database for changes (count and newest stamp of the model's embeddings) and loads only what changed, so every
/// server stays current without messages. Unused tenants are dropped after 30 minutes.
/// </summary>
internal sealed class VectorIndex(IMemoryCache cache)
{
    /// <summary>Embeddings stored within this window before the newest known one are read again (clock skew between servers).</summary>
    private static readonly long SkewTicks = TimeSpan.FromSeconds(30).Ticks;

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
            var state = await db.Passages.AsNoTracking()
                .Where(p => p.EmbeddingModel == model && p.Embedding != null)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), Newest = g.Max(p => p.VectorStamp) })
                .FirstOrDefaultAsync(ct);
            var count = state?.Count ?? 0;
            var newest = state?.Newest ?? 0;
            if (count != current.Count || newest != current.Newest)
            {
                current = await RefreshAsync(db, current, count, newest, ct);
            }

            cache.Set(key, current, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<VectorSnapshot> RefreshAsync(SearchDbContext db, VectorSnapshot current, int count, long newest, CancellationToken ct)
    {
        var entries = current.Entries.ToDictionary(e => e.PassageId);
        var since = current.Newest - SkewTicks;
        var changed = await (from p in db.Passages.AsNoTracking()
                             where p.EmbeddingModel == current.Model && p.Embedding != null && p.VectorStamp > since
                             join d in db.Documents.AsNoTracking() on p.DocumentId equals d.Id
                             select new { p.Id, p.DocumentId, d.WorkspaceId, d.ContainerId, p.Embedding })
            .ToListAsync(ct);
        foreach (var row in changed)
        {
            entries[row.Id] = new VectorEntry(row.Id, row.DocumentId, row.WorkspaceId, row.ContainerId, Vectors.FromBytes(row.Embedding!));
        }

        if (entries.Count != count)
        {
            // Passages were deleted (or documents are gone): keep only what still exists.
            var ids = (await db.Passages.AsNoTracking().Where(p => p.EmbeddingModel == current.Model && p.Embedding != null).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
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

/// <summary>
/// Embeds passages that have no embedding of the configured model yet (SRC-07): new or changed text, or all passages
/// after the model changed. Runs per tenant on <see cref="SearchOptions.EmbeddingSchedule"/>; when the provider fails
/// it stops and tries again next time.
/// </summary>
internal sealed partial class EmbeddingJob(
    SearchDbContext db, SemanticSearch semantic, IOptions<SearchOptions> options, TimeProvider time, ILogger<EmbeddingJob> logger) : ITenantRecurringJob
{
    public const string Name = "search.embeddings";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!semantic.Enabled)
        {
            return;
        }

        var model = semantic.ModelKey;
        var batchSize = Math.Clamp(options.Value.EmbeddingBatchSize, 1, 2048);
        for (var done = 0; done < options.Value.EmbeddingsPerRun;)
        {
            var batch = await (from p in db.Passages
                               where p.EmbeddingModel != model
                               join d in db.Documents on p.DocumentId equals d.Id
                               orderby p.Id
                               select new { Passage = p, d.Title })
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return;
            }

            GeneratedEmbeddings<Embedding<float>> embeddings;
            try
            {
                embeddings = await semantic.Generator!.GenerateAsync(
                    batch.Select(b => Passages.EmbeddingInput(b.Title, b.Passage.Text)), cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Any provider failure (network, quota, bad response): try again on the next run.
                LogEmbeddingFailed(ex, model);
                return;
            }

            var stamp = time.GetUtcNow().UtcTicks;
            foreach (var (item, embedding) in batch.Zip(embeddings))
            {
                item.Passage.Embedding = Vectors.ToBytes(embedding.Vector.Span);
                item.Passage.EmbeddingModel = model;
                item.Passage.VectorStamp = stamp;
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            done += batch.Count;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding passages with {Model} failed; retrying on the next run.")]
    private partial void LogEmbeddingFailed(Exception exception, string model);
}
