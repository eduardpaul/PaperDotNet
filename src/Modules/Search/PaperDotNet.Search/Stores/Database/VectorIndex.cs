using System.Collections.Concurrent;
using System.Numerics.Tensors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PaperDotNet.Search.Data;
using PaperDotNet.Search.Features;

namespace PaperDotNet.Search.Stores.Database;

/// <summary>A semantic match: the document and its most similar passage.</summary>
internal sealed record SemanticMatch(Guid DocumentId, Guid PassageId, float Similarity);

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
