using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Search.Features;

namespace PaperDotNet.Search.Stores.Database;

/// <summary>
/// The default search store (ADR-0012, ADR-0027, ADR-0043): documents, tags and passages in <see cref="SearchDbContext"/>,
/// full text through the provider's <see cref="IFullTextSearch"/> (FTS5 or <c>tsvector</c>), and embeddings stored
/// with the passages and searched in memory (<see cref="VectorIndex"/>). Counts and facets are exact.
/// </summary>
internal sealed class DatabaseSearchStore(SearchDbContext db, IFullTextSearch fullText, VectorIndex vectors, ITenantContext tenant, TimeProvider time)
    : ISearchStore
{
    public const string StoreName = "database";

    /// <summary>Body text beyond this is not indexed (keeps rows and FTS indexes reasonable).</summary>
    public const int MaxBodyLength = 200_000;

    /// <summary>Passages looked at per semantic query; several may belong to one document.</summary>
    private const int PassageCandidates = 1000;

    private const int FacetSize = 20;
    private const int TermChunkSize = 500;

    public string Name => StoreName;

    public SearchStoreCapabilities Capabilities => SearchStoreCapabilities.Keyword | SearchStoreCapabilities.Vector | SearchStoreCapabilities.Facets;

    public async Task UpsertAsync(IReadOnlyCollection<SearchDocumentData> documents, CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return;
        }

        // The same item can be indexed from two places at once (e.g. an event subscriber and a request): the loser of
        // the race hits a key conflict and simply writes again on top of the winner.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WriteAsync(documents, cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task WriteAsync(IReadOnlyCollection<SearchDocumentData> documents, CancellationToken cancellationToken)
    {
        var ids = documents.Select(d => d.Id).ToList();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Tags.Where(t => ids.Contains(t.DocumentId)).ExecuteDeleteAsync(cancellationToken);
        var existing = await db.Documents.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);
        var passages = (await db.Passages.Where(p => ids.Contains(p.DocumentId)).ToListAsync(cancellationToken)).ToLookup(p => p.DocumentId);
        foreach (var data in documents)
        {
            if (!existing.TryGetValue(data.Id, out var document))
            {
                document = new SearchDocument { Id = data.Id, SourceType = data.SourceType, Title = data.Title };
                db.Documents.Add(document);
            }

            document.SourceType = data.SourceType;
            document.WorkspaceId = data.WorkspaceId;
            document.ContainerId = data.ContainerId;
            document.ContentTypeId = data.ContentTypeId;
            document.ScopeId = data.ScopeId;
            document.Title = data.Title.Length > 1024 ? data.Title[..1024] : data.Title;
            var body = data.Pages.Count == 0 ? data.Body : $"{data.Body}\n{string.Join('\n', data.Pages)}";
            document.Body = body.Length > MaxBodyLength ? body[..MaxBodyLength] : body;
            document.Keywords = data.Keywords.Length > MaxBodyLength ? data.Keywords[..MaxBodyLength] : data.Keywords;
            document.Language = FullTextLanguages.All.Contains(data.Language ?? string.Empty) ? data.Language : null;
            document.CreatedBy = data.CreatedBy;
            document.UpdatedAt = data.UpdatedAt;
            db.Tags.AddRange(data.TermIds.Distinct().Select(t => new SearchTag { DocumentId = data.Id, TermId = t }));
            UpdatePassages(data, document.Title, document.Language, passages[data.Id]);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Replaces the document's passages. A passage whose embedded text is unchanged keeps its row and embedding
    /// (moved to its new position), so re-indexing does not embed it again.
    /// </summary>
    private void UpdatePassages(SearchDocumentData data, string title, string? language, IEnumerable<SearchPassage> current)
    {
        var reusable = current.GroupBy(p => p.ContentHash).ToDictionary(g => g.Key, g => new Queue<SearchPassage>(g));
        foreach (var (passage, ordinal) in Passages.Split(data).Select((p, i) => (p, i)))
        {
            var hash = Passages.Hash(Passages.EmbeddingInput(title, passage.Text));
            if (reusable.TryGetValue(hash, out var queue) && queue.TryDequeue(out var kept))
            {
                kept.Ordinal = ordinal;
                kept.Page = passage.Page;
                kept.Language = language;
                continue;
            }

            db.Passages.Add(new SearchPassage
            {
                Id = Ids.New(),
                DocumentId = data.Id,
                Ordinal = ordinal,
                Page = passage.Page,
                Text = passage.Text,
                Language = language,
                ContentHash = hash,
            });
        }

        db.Passages.RemoveRange(reusable.Values.SelectMany(q => q));
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await db.Passages.Where(p => ids.Contains(p.DocumentId)).ExecuteDeleteAsync(cancellationToken);
        await db.Tags.Where(t => ids.Contains(t.DocumentId)).ExecuteDeleteAsync(cancellationToken);
        await db.Documents.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task SetScopesAsync(IReadOnlyDictionary<Guid, Guid> scopes, CancellationToken cancellationToken)
    {
        foreach (var group in scopes.GroupBy(s => s.Value, s => s.Key))
        {
            var scopeId = group.Key;
            foreach (var chunk in group.Chunk(1000))
            {
                await db.Documents.Where(d => chunk.Contains(d.Id) && d.ScopeId != scopeId)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.ScopeId, scopeId), cancellationToken);
            }
        }
    }

    public async Task DeleteContainerAsync(Guid containerId, CancellationToken cancellationToken) =>
        await DeleteWhereAsync(db.Documents.Where(d => d.ContainerId == containerId), cancellationToken);

    public async Task DeleteSourceAsync(string sourceType, CancellationToken cancellationToken) =>
        await DeleteWhereAsync(db.Documents.Where(d => d.SourceType == sourceType), cancellationToken);

    private async Task DeleteWhereAsync(IQueryable<SearchDocument> documents, CancellationToken ct)
    {
        await db.Passages.Where(p => documents.Any(d => d.Id == p.DocumentId)).ExecuteDeleteAsync(ct);
        await db.Tags.Where(t => documents.Any(d => d.Id == t.DocumentId)).ExecuteDeleteAsync(ct);
        await documents.ExecuteDeleteAsync(ct);
    }

    /// <summary>Counts tag usage from the index (one row per document and term).</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountTermsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var chunk in termIds.Distinct().Chunk(TermChunkSize))
        {
            var counts = await db.Tags.AsNoTracking()
                .Where(t => chunk.Contains(t.TermId))
                .GroupBy(t => t.TermId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            foreach (var count in counts)
            {
                result[count.Key] = count.Count;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<PassageToEmbed>> GetPassagesToEmbedAsync(string model, int limit, CancellationToken cancellationToken) =>
        (await (from p in db.Passages.AsNoTracking()
                where p.EmbeddingModel != model
                join d in db.Documents.AsNoTracking() on p.DocumentId equals d.Id
                orderby p.Id
                select new { p.Id, d.Title, p.Text })
            .Take(limit)
            .ToListAsync(cancellationToken))
        .Select(p => new PassageToEmbed(p.Id, Passages.EmbeddingInput(p.Title, p.Text)))
        .ToList();

    public async Task SetEmbeddingsAsync(string model, IReadOnlyCollection<PassageEmbedding> embeddings, CancellationToken cancellationToken)
    {
        var byId = embeddings.ToDictionary(e => e.PassageId);
        var ids = byId.Keys.ToList();
        var stamp = time.GetUtcNow().UtcTicks;
        foreach (var passage in await db.Passages.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken))
        {
            passage.Embedding = Vectors.ToBytes(byId[passage.Id].Vector.Span);
            passage.EmbeddingModel = model;
            passage.VectorStamp = stamp;
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public async Task<StoreSearchResult> SearchAsync(StoreSearchQuery query, CancellationToken cancellationToken)
    {
        if (query.Mode != SearchMode.Keyword && (query.Vector is null || query.VectorModel is null))
        {
            throw new ArgumentException("Semantic and hybrid search need the query vector.", nameof(query));
        }

        var filtered = Filtered(query.Filter);
        return query.Mode == SearchMode.Keyword
            ? await KeywordAsync(query, filtered, cancellationToken)
            : await FusedAsync(query, filtered, cancellationToken);
    }

    /// <summary>Documents the caller may read that match the filters.</summary>
    private IQueryable<SearchDocument> Filtered(StoreFilter filter)
    {
        // Trimmed by the scopes the caller can read in the whole tenant (ADR-0035): one parameter on both databases.
        var readable = filter.ReadableScopes.ToArray();
        var documents = db.Documents.AsNoTracking().Where(d => EF.Parameter(readable).Contains(d.ScopeId));
        if (filter.WorkspaceId is { } ws)
        {
            documents = documents.Where(d => d.WorkspaceId == ws);
        }

        if (filter.ContainerId is { } container)
        {
            documents = documents.Where(d => d.ContainerId == container);
        }

        if (filter.ContentTypeId is { } contentType)
        {
            documents = documents.Where(d => d.ContentTypeId == contentType);
        }

        if (filter.CreatedBy is { } author)
        {
            documents = documents.Where(d => d.CreatedBy == author);
        }

        if (filter.UpdatedFrom is { } from)
        {
            documents = documents.Where(d => d.UpdatedAt >= from);
        }

        if (filter.UpdatedTo is { } to)
        {
            documents = documents.Where(d => d.UpdatedAt < to);
        }

        if (filter.TermIds is { Count: > 0 } terms)
        {
            var termIds = terms.ToArray();
            documents = documents.Where(d => db.Tags.Any(t => t.DocumentId == d.Id && termIds.Contains(t.TermId)));
        }

        return documents;
    }

    /// <summary>Full text (or filters only): exact count and facets over every match, paged by rank.</summary>
    private async Task<StoreSearchResult> KeywordAsync(StoreSearchQuery query, IQueryable<SearchDocument> filtered, CancellationToken ct)
    {
        var text = query.Text;
        var hits = text is null
            ? filtered.Select(d => new { Document = d, Rank = 0.0 })
            : from d in filtered
              join m in fullText.Match<SearchDocument>(db, text) on d.Id equals m.Id
              select new { Document = d, m.Rank };

        var count = await hits.CountAsync(ct);
        var facets = query.WithFacets ? await FacetsAsync(hits.Select(h => h.Document), ct) : null;
        var ordered = text is null
            ? hits.OrderByDescending(h => h.Document.UpdatedAt).ThenBy(h => h.Document.Id)
            : hits.OrderByDescending(h => h.Rank).ThenBy(h => h.Document.Id);
        var page = await ordered.Skip(query.Skip).Take(query.Top).ToListAsync(ct);
        var matchedBy = text is null ? Array.Empty<string>() : [SearchMatch.Keyword];
        var ranked = page.Select(h => (h.Document, h.Rank, matchedBy)).ToList();
        return new StoreSearchResult(await WithPassagesAsync(ranked, text, [], ct), count, facets);
    }

    /// <summary>
    /// Semantic or hybrid: the best <see cref="StoreSearchQuery.CandidateLimit"/> documents of each side, fused by
    /// reciprocal rank; count and facets cover the fused candidates.
    /// </summary>
    private async Task<StoreSearchResult> FusedAsync(StoreSearchQuery query, IQueryable<SearchDocument> filtered, CancellationToken ct)
    {
        var limit = Math.Max(1, query.CandidateLimit);
        var keyword = new List<Guid>();
        if (query.Mode == SearchMode.Hybrid && query.Text is { } text)
        {
            keyword = await (from d in filtered
                             join m in fullText.Match<SearchDocument>(db, text) on d.Id equals m.Id
                             orderby m.Rank descending, d.Id
                             select d.Id).Take(limit).ToListAsync(ct);
        }

        var semanticMatches = await SemanticAsync(query, filtered, limit, ct);
        var scores = new Dictionary<Guid, double>();
        void Fuse(IEnumerable<Guid> ids)
        {
            foreach (var (id, index) in ids.Select((id, i) => (id, i)))
            {
                scores[id] = scores.GetValueOrDefault(id) + (1.0 / (SearchMatch.FusionK + index + 1));
            }
        }

        Fuse(keyword);
        Fuse(semanticMatches.Select(m => m.DocumentId));
        var ordered = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key).ToList();
        var pageIds = ordered.Skip(query.Skip).Take(query.Top).Select(s => s.Key).ToList();
        var documents = await db.Documents.AsNoTracking().Where(d => pageIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var keywordIds = keyword.ToHashSet();
        var semanticIds = semanticMatches.ToDictionary(m => m.DocumentId);
        var ranked = pageIds.Where(documents.ContainsKey).Select(id =>
        {
            string[] matchedBy = keywordIds.Contains(id) && semanticIds.ContainsKey(id) ? [SearchMatch.Keyword, SearchMatch.Semantic]
                : keywordIds.Contains(id) ? [SearchMatch.Keyword] : [SearchMatch.Semantic];
            return (documents[id], scores[id], matchedBy);
        }).ToList();

        SearchFacets? facets = null;
        if (query.WithFacets)
        {
            var all = ordered.Select(s => s.Key).ToList();
            facets = await FacetsAsync(db.Documents.AsNoTracking().Where(d => all.Contains(d.Id)), ct);
        }

        return new StoreSearchResult(await WithPassagesAsync(ranked, query.Text, semanticIds, ct), ordered.Count, facets);
    }

    /// <summary>Semantic matches the caller may read that pass the filters and contain no excluded word, best first.</summary>
    private async Task<List<SemanticMatch>> SemanticAsync(StoreSearchQuery query, IQueryable<SearchDocument> filtered, int limit, CancellationToken ct)
    {
        var matches = await NearestAsync(query, ct);
        if (matches.Count == 0)
        {
            return matches;
        }

        var candidates = matches.Select(m => m.DocumentId).ToList();
        var allowed = (await filtered.Where(d => candidates.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        if (query.Text is { Excluded.Count: > 0 } text)
        {
            var excluded = new FullTextQuery([text.Excluded], []);
            allowed.ExceptWith(await fullText.Match<SearchDocument>(db, excluded).Where(m => candidates.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct));
        }

        return [.. matches.Where(m => allowed.Contains(m.DocumentId)).Take(limit)];
    }

    /// <summary>The documents most similar to the query vector, best first, optionally only in a workspace or container.</summary>
    private async Task<List<SemanticMatch>> NearestAsync(StoreSearchQuery query, CancellationToken ct)
    {
        var vector = query.Vector!.Value.ToArray();
        Vectors.Normalize(vector);
        var snapshot = await vectors.GetAsync(db, tenant.TenantId!.Value, query.VectorModel!, ct);
        var best = new Dictionary<Guid, SemanticMatch>();
        foreach (var match in snapshot.Nearest(vector, PassageCandidates, query.MinSimilarity, query.Filter.WorkspaceId, query.Filter.ContainerId))
        {
            if (!best.TryGetValue(match.DocumentId, out var current) || current.Similarity < match.Similarity)
            {
                best[match.DocumentId] = match;
            }
        }

        return [.. best.Values.OrderByDescending(m => m.Similarity).ThenBy(m => m.DocumentId)];
    }

    /// <summary>
    /// Hits with the passage that matched best: for keyword matches the passage with the best full-text rank, else the
    /// most similar one. The hit's text comes from that passage and it points to its page (SRC-09).
    /// </summary>
    private async Task<List<StoreHit>> WithPassagesAsync(
        List<(SearchDocument Document, double Rank, string[] MatchedBy)> ranked, FullTextQuery? text,
        Dictionary<Guid, SemanticMatch> semanticMatches, CancellationToken ct)
    {
        var ids = ranked.Select(r => r.Document.Id).ToList();
        var passages = new Dictionary<Guid, (int? Page, string Text)>();
        if (text is not null && ranked.Any(r => r.MatchedBy.Contains(SearchMatch.Keyword)))
        {
            var matches = await (from p in db.Passages.AsNoTracking()
                                 join m in fullText.Match<SearchPassage>(db, text) on p.Id equals m.Id
                                 where ids.Contains(p.DocumentId)
                                 select new { p.DocumentId, p.Ordinal, p.Page, p.Text, m.Rank }).ToListAsync(ct);
            foreach (var best in matches.GroupBy(m => m.DocumentId).Select(g => g.OrderByDescending(m => m.Rank).ThenBy(m => m.Ordinal).First()))
            {
                passages[best.DocumentId] = (best.Page, best.Text);
            }
        }

        var similar = ids.Where(id => !passages.ContainsKey(id) && semanticMatches.ContainsKey(id)).Select(id => semanticMatches[id].PassageId).ToList();
        if (similar.Count > 0)
        {
            foreach (var p in await db.Passages.AsNoTracking().Where(p => similar.Contains(p.Id)).Select(p => new { p.DocumentId, p.Page, p.Text }).ToListAsync(ct))
            {
                passages[p.DocumentId] = (p.Page, p.Text);
            }
        }

        return ranked.Select(r =>
        {
            var d = r.Document;
            var passage = passages.GetValueOrDefault(d.Id);
            return new StoreHit(d.Id, d.SourceType, d.WorkspaceId, d.ContainerId, d.ContentTypeId, d.Title, r.Rank, d.CreatedBy, d.UpdatedAt)
            {
                Page = passage.Page,
                Text = passage.Text ?? d.Body,
                MatchedBy = r.MatchedBy,
            };
        }).ToList();
    }

    private async Task<SearchFacets> FacetsAsync(IQueryable<SearchDocument> documents, CancellationToken ct)
    {
        var ids = documents.Select(d => d.Id);
        return new SearchFacets(
            await FacetAsync(documents, d => d.WorkspaceId, ct),
            await FacetAsync(documents.Where(d => d.ContainerId != null), d => d.ContainerId!.Value, ct),
            await FacetAsync(documents.Where(d => d.ContentTypeId != null), d => d.ContentTypeId!.Value, ct),
            await FacetAsync(db.Tags.Where(t => ids.Contains(t.DocumentId)), t => t.TermId, ct));
    }

    /// <summary>The most frequent values of <paramref name="key"/> with their counts.</summary>
    private static async Task<List<FacetValue>> FacetAsync<T>(IQueryable<T> source, Expression<Func<T, Guid>> key, CancellationToken ct) =>
        (await source.GroupBy(key)
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(v => v.Count).ThenBy(v => v.Key)
            .Take(FacetSize)
            .ToListAsync(ct))
        .Select(v => new FacetValue(v.Key, v.Count))
        .ToList();
}
