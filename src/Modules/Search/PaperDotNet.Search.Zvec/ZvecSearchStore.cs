using System.Text.Json;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Zvec.Native;
using static PaperDotNet.Search.Zvec.ZvecLayout;

namespace PaperDotNet.Search.Zvec;

/// <summary>
/// The zvec search store (ADR-0044): one in-process zvec collection per tenant holds every document's text, metadata
/// and passage vectors, and searches run in zvec (full text with BM25 and stemming, HNSW vectors, filters on the
/// metadata and fields). Results are documents like those of the database store: keyword matches use all of a
/// document's text, title and keyword matches rank higher, semantic matches come from the most similar passage, and
/// hybrid search fuses both by reciprocal rank. Counts and facets cover the candidates
/// (<see cref="ZvecOptions.KeywordCandidates"/>).
/// </summary>
internal sealed class ZvecSearchStore(ZvecCollections collections, ITenantContext tenant, ISearchVectorSpace vectorSpace, IOptions<ZvecOptions> options)
    : ISearchStore
{
    public const string StoreName = "zvec";

    /// <summary>Text beyond this is not indexed, as in the database store.</summary>
    public const int MaxBodyLength = 200_000;

    private const int FacetSize = 20;
    private const double HeadingWeight = 10;

    private static readonly (string, uint)[] HeadOutputs =
    [
        (DocumentId, ZvecNative.TypeString), (SourceType, ZvecNative.TypeString), (WorkspaceId, ZvecNative.TypeString),
        (ContainerId, ZvecNative.TypeString), (ContentTypeId, ZvecNative.TypeString), (Title, ZvecNative.TypeString),
        (CreatedBy, ZvecNative.TypeString), (UpdatedAt, ZvecNative.TypeInt64), (TermIds, ZvecNative.TypeArrayString),
    ];

    private static readonly (string, uint)[] PassageOutputs =
    [
        (DocumentId, ZvecNative.TypeString), (Ordinal, ZvecNative.TypeInt32), (Page, ZvecNative.TypeInt32), (Text, ZvecNative.TypeString),
    ];

    public string Name => StoreName;

    public SearchStoreCapabilities Capabilities => SearchStoreCapabilities.Keyword | SearchStoreCapabilities.Vector;

    /// <summary>The tenant's collection for writing (created when missing).</summary>
    private async Task<ZvecLease> LeaseAsync(CancellationToken ct) =>
        (await collections.LeaseAsync(TenantId, await vectorSpace.GetAsync(ct), create: true, ct))!;

    /// <summary>The tenant's collection for reading, or null when the tenant has none yet.</summary>
    private async Task<ZvecLease?> ReadLeaseAsync(CancellationToken ct) =>
        await collections.LeaseAsync(TenantId, await vectorSpace.GetAsync(ct), create: false, ct);

    private Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("Search needs a tenant.");

    private static async Task WriteAsync(ZvecIndex index, Action write, CancellationToken ct)
    {
        await index.WriteLock.WaitAsync(ct);
        try
        {
            write();
        }
        finally
        {
            index.WriteLock.Release();
        }
    }

    private string StagePath(Guid id) => System.IO.Path.Combine(collections.Path, Id(TenantId), "staged", $"{id:N}.json");

    public async Task StageAsync(SearchGeneration generation, CancellationToken cancellationToken)
    {
        var path = StagePath(generation.Id);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + $".{Ids.New():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(generation), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async Task<SearchGeneration?> GetStageAsync(Guid generationId, CancellationToken cancellationToken)
    {
        try { return JsonSerializer.Deserialize<SearchGeneration>(await File.ReadAllTextAsync(StagePath(generationId), cancellationToken)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public Task DeleteStageAsync(Guid generationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(StagePath(generationId));
        return Task.CompletedTask;
    }

    public async Task UpsertAsync(IReadOnlyCollection<SearchDocumentData> documents, CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return;
        }

        using var lease = await LeaseAsync(cancellationToken);
        var index = lease.Index;
        var added = 0;
        await WriteAsync(index, () =>
        {
            foreach (var chunk in documents.DistinctBy(d => d.Id).Chunk(100))
            {
                added += Write(index, chunk);
            }
        }, cancellationToken);
        if (added > 0 && index.Vectors is not null)
        {
            collections.SetEmbedded(TenantId, done: false);
        }
    }

    /// <summary>
    /// Writes the head rows again and replaces the passages. A passage whose embedded text is unchanged keeps its row
    /// and vector (its position and metadata are updated), so re-indexing does not embed it again. Returns the number
    /// of new passages (they need embeddings).
    /// </summary>
    private static int Write(ZvecIndex index, SearchDocumentData[] documents)
    {
        var collection = index.Collection;
        foreach (var document in documents)
        {
            index.Catalog.Register(document.Fields, collection.AddNumberColumn);
        }

        var numberColumns = index.Catalog.NumberColumns.Select(NumberColumn).ToList();
        var existing = collection.Text(Heading, AllToken, PassagesOf(documents.Select(d => d.Id)), ZvecCollection.MaxTopK,
                [(DocumentId, ZvecNative.TypeString), (ContentHash, ZvecNative.TypeString)])
            .ToLookup(h => h.Text(DocumentId)!);
        var upserts = new List<ZvecRow>();
        var updates = new List<ZvecRow>();
        var deletes = new List<string>();
        foreach (var document in documents)
        {
            var metadata = Metadata.Of(document);
            var title = document.Title.Length > 1024 ? document.Title[..1024] : document.Title;
            var keywords = Cut(document.Keywords);
            var body = Cut($"{document.Body}\n{string.Join('\n', document.Chunks.Where(p => p.Page != null || !p.FromBody).Select(p => p.Text))}");
            var content = $"{title}\n{keywords}\n{body}";
            var language = LanguageOf(document) is { } l && index.Catalog.Languages.Contains(l) ? l : null;
            var head = metadata.Write(new ZvecRow(Id(document.Id)), numberColumns, clear: false)
                .Text(Kind, DocumentKind).Text(Title, title).Text(Heading, $"{AllToken} {title}\n{keywords}").Text(Content, content);
            if (index.Catalog.Prefix)
            {
                head.Text(Prefix, content);
            }

            if (language is not null)
            {
                head.Text(Language, language).Text(ContentIn(language), content);
            }

            upserts.Add(head);
            var reusable = existing[Id(document.Id)].GroupBy(h => h.Text(ContentHash) ?? string.Empty)
                .ToDictionary(g => g.Key, g => new Queue<string>(g.Select(h => h.Pk)));
            foreach (var (passage, ordinal) in document.Chunks.Select((p, i) => (p, i)))
            {
                var hash = Passages.Hash(passage.Text);
                string? kept = null;
                if (reusable.TryGetValue(hash, out var queue) && queue.TryDequeue(out var reused))
                {
                    kept = reused;
                }

                var row = kept is not null
                    ? metadata.Write(new ZvecRow(kept), numberColumns, clear: true)
                    : metadata.Write(new ZvecRow(Id(Ids.New())), numberColumns, clear: false)
                        .Text(Kind, PassageKind).Text(Heading, AllToken).Text(Text, passage.Text).Text(Content, passage.Text).Text(ContentHash, hash);
                if (kept is null && language is not null)
                {
                    row.Text(ContentIn(language), passage.Text);
                }

                row.Int(Ordinal, ordinal);
                if (passage.Page is { } page)
                {
                    row.Int(Page, page);
                }
                else if (kept is not null)
                {
                    row.Null(Page);
                }

                (kept is null ? upserts : updates).Add(row);
            }

            deletes.AddRange(reusable.Values.SelectMany(q => q));
        }

        if (deletes.Count > 0)
        {
            collection.Delete(deletes);
        }

        var added = upserts.Count - documents.Length;
        collection.Upsert(upserts);
        collection.Update(updates);
        return added;
    }

    private static string Cut(string text) => text.Length > MaxBodyLength ? text[..MaxBodyLength] : text;

    private static string? LanguageOf(SearchDocumentData document) =>
        document.Language is { } language && Languages.Contains(language) ? language : null;

    private static string PassagesOf(IEnumerable<Guid> documentIds) =>
        ZvecFilter.And([ZvecFilter.Equal(Kind, PassageKind), ZvecFilter.In(DocumentId, documentIds.Select(Id))]);

    /// <summary>The metadata every row of a document carries, so one filter covers head and passage rows.</summary>
    private sealed record Metadata(
        SearchDocumentData Document, IReadOnlyList<string> Names, IReadOnlyList<string> Tokens, IReadOnlyDictionary<string, double> Numbers)
    {
        public static Metadata Of(SearchDocumentData document)
        {
            var names = new List<string> { Sentinel };
            var tokens = new List<string> { Sentinel };
            var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var field in document.Fields.Where(f => f.Name.Length is > 0 and <= 128))
            {
                var key = FieldKey(field.Name, field.Kind);
                if (SearchField.IsText(field.Kind) || field.Kind == SearchFieldKind.Boolean)
                {
                    var values = field.Values
                        .Where(v => field.Kind == SearchFieldKind.Boolean ? v.Number is not null : v.Text is { Length: > 0 and <= SearchField.MaxTextLength })
                        .Select(v => Token(key, field.Kind, v))
                        .ToList();
                    if (values.Count > 0)
                    {
                        names.Add(key);
                        tokens.AddRange(values);
                    }
                }
                else if (field.Values.Select(v => v.Number).FirstOrDefault(n => n is { } d && double.IsFinite(d)) is { } number)
                {
                    names.Add(key);
                    numbers[NumberColumn(key)] = number;
                }
            }

            return new Metadata(document, [.. names.Distinct(StringComparer.Ordinal)], [.. tokens.Distinct(StringComparer.Ordinal)], numbers);
        }

        /// <summary>Sets the metadata columns; with <paramref name="clear"/> (an update) it empties those without a value.</summary>
        public ZvecRow Write(ZvecRow row, IReadOnlyList<string> numberColumns, bool clear)
        {
            var d = Document;
            row.Text(DocumentId, Id(d.Id)).Text(SourceType, d.SourceType).Text(WorkspaceId, Id(d.WorkspaceId)).Text(ScopeId, Id(d.ScopeId))
                .Long(UpdatedAt, d.UpdatedAt.UtcTicks).Texts(TermIds, [Sentinel, .. d.TermIds.Distinct().Select(Id)])
                .Texts(FieldNames, Names).Texts(FieldTokens, Tokens);
            Optional(row, ContainerId, d.ContainerId, clear);
            Optional(row, ContentTypeId, d.ContentTypeId, clear);
            Optional(row, CreatedBy, d.CreatedBy, clear);
            foreach (var column in numberColumns)
            {
                if (Numbers.TryGetValue(column, out var value))
                {
                    row.Double(column, value);
                }
                else if (clear)
                {
                    row.Null(column);
                }
            }

            return row;
        }

        private static void Optional(ZvecRow row, string column, Guid? value, bool clear)
        {
            if (value is { } id)
            {
                row.Text(column, Id(id));
            }
            else if (clear)
            {
                row.Null(column);
            }
        }
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null)
        {
            return;
        }

        var index = lease.Index;
        await WriteAsync(index, () =>
        {
            foreach (var chunk in ids.Distinct().Chunk(1000))
            {
                index.Collection.DeleteWhere(ZvecFilter.In(DocumentId, chunk.Select(Id)));
            }
        }, cancellationToken);
    }

    public async Task DeleteContainerAsync(Guid containerId, CancellationToken cancellationToken)
    {
        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null)
        {
            return;
        }

        var index = lease.Index;
        await WriteAsync(index, () => index.Collection.DeleteWhere(ZvecFilter.Equal(ContainerId, Id(containerId))), cancellationToken);
    }

    public async Task DeleteSourceAsync(string sourceType, CancellationToken cancellationToken)
    {
        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null)
        {
            return;
        }

        var index = lease.Index;
        await WriteAsync(index, () => index.Collection.DeleteWhere(ZvecFilter.Equal(SourceType, sourceType)), cancellationToken);
    }

    public async Task SetScopesAsync(IReadOnlyDictionary<Guid, Guid> scopes, CancellationToken cancellationToken)
    {
        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null)
        {
            return;
        }

        var index = lease.Index;
        await WriteAsync(index, () =>
        {
            var collection = index.Collection;
            foreach (var chunk in scopes.Chunk(1000))
            {
                var heads = collection.Fetch(chunk.Select(s => Id(s.Key)).ToList(), [(DocumentId, ZvecNative.TypeString)]).Select(h => h.Pk).ToHashSet(StringComparer.Ordinal);
                var scopeOf = chunk.Where(s => heads.Contains(Id(s.Key))).ToDictionary(s => Id(s.Key), s => Id(s.Value), StringComparer.Ordinal);
                if (scopeOf.Count == 0)
                {
                    continue;
                }

                var passages = collection.Text(Heading, AllToken, PassagesOf(scopeOf.Keys.Select(Guid.Parse)), ZvecCollection.MaxTopK, [(DocumentId, ZvecNative.TypeString)]);
                collection.Update(
                [
                    .. scopeOf.Select(s => new ZvecRow(s.Key).Text(ScopeId, s.Value)),
                    .. passages.Select(p => new ZvecRow(p.Pk).Text(ScopeId, scopeOf[p.Text(DocumentId)!])),
                ]);
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchFieldInfo>> GetFieldsAsync(CancellationToken cancellationToken)
    {
        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null)
        {
            return [];
        }

        var index = lease.Index;
        return index.Catalog.Fields
            .Where(f => index.Collection.Text(Heading, AllToken, Heads(ZvecFilter.ContainsAny(FieldNames, [f.Key])), 1, []).Count > 0)
            .Select(f => f.Field)
            .OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Kind)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountTermsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken)
    {
        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null)
        {
            return new Dictionary<Guid, int>();
        }

        var index = lease.Index;
        var counts = new Dictionary<Guid, int>();
        foreach (var term in termIds.Distinct())
        {
            var count = index.Collection.Text(Heading, AllToken, Heads(ZvecFilter.ContainsAny(TermIds, [Id(term)])), ZvecCollection.MaxTopK, []).Count;
            if (count > 0)
            {
                counts[term] = count;
            }
        }

        return counts;
    }

    public async Task<IReadOnlyList<PassageToEmbed>> GetPassagesToEmbedAsync(string model, int limit, CancellationToken cancellationToken)
    {
        // The embedding job asks every tenant every minute: skip those known to be done without opening their collection.
        if (!collections.MayNeedEmbeddings(TenantId))
        {
            return [];
        }

        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null || lease.Index.Vectors?.Key != model)
        {
            collections.SetEmbedded(TenantId, done: true);
            return [];
        }

        var index = lease.Index;
        var filter = ZvecFilter.And(
        [
            ZvecFilter.Equal(Kind, PassageKind),
            ZvecFilter.Or([$"{EmbeddingModel} IS NULL", $"{EmbeddingModel} != {ZvecFilter.Quote(index.Catalog.VectorModel!)}"]),
        ]);
        var passages = index.Collection.Text(Heading, AllToken, filter, Math.Clamp(limit, 1, ZvecCollection.MaxTopK), [(DocumentId, ZvecNative.TypeString), (Text, ZvecNative.TypeString)]);
        if (passages.Count == 0)
        {
            collections.SetEmbedded(TenantId, done: true);
        }

        var titles = index.Collection.Fetch(passages.Select(p => p.Text(DocumentId)!).Distinct().ToList(), [(Title, ZvecNative.TypeString)])
            .ToDictionary(h => h.Pk, h => h.Text(Title) ?? string.Empty, StringComparer.Ordinal);
        return passages
            .Select(p => new PassageToEmbed(Guid.Parse(p.Pk), Passages.EmbeddingInput(titles.GetValueOrDefault(p.Text(DocumentId)!, string.Empty), p.Text(Text) ?? string.Empty)))
            .ToList();
    }

    public async Task<IReadOnlyList<PassageToEmbed>> GetPassagesToEmbedAsync(Guid documentId, string model, int limit, CancellationToken cancellationToken)
    {
        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null || lease.Index.Vectors?.Key != model)
        {
            return [];
        }
        var index = lease.Index;
        var filter = ZvecFilter.And([PassagesOf([documentId]), ZvecFilter.Or([$"{EmbeddingModel} IS NULL", $"{EmbeddingModel} != {ZvecFilter.Quote(index.Catalog.VectorModel!)}"])]);
        return index.Collection.Text(Heading, AllToken, filter, limit, [(Text, ZvecNative.TypeString)])
            .Select(p => new PassageToEmbed(Guid.Parse(p.Pk), p.Text(Text) ?? string.Empty)).ToList();
    }

    public async Task SetEmbeddingsAsync(string model, IReadOnlyCollection<PassageEmbedding> embeddings, CancellationToken cancellationToken)
    {
        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null)
        {
            return;
        }

        var index = lease.Index;
        if (index.Vectors?.Key != model || embeddings.Count == 0)
        {
            return;
        }

        await WriteAsync(index, () =>
        {
            var byId = embeddings.ToDictionary(e => Id(e.PassageId), StringComparer.Ordinal);
            var found = index.Collection.Fetch(byId.Keys.ToList(), [(Kind, ZvecNative.TypeString)]).Select(h => h.Pk);
            index.Collection.Update([.. found.Select(pk => new ZvecRow(pk).Vector(Embedding, byId[pk].Vector.Span).Text(EmbeddingModel, index.Catalog.VectorModel!))]);
        }, cancellationToken);
    }

    private static string Heads(string condition) => ZvecFilter.And([ZvecFilter.Equal(Kind, DocumentKind), condition]);

    public async Task<StoreSearchResult> SearchAsync(StoreSearchQuery query, CancellationToken cancellationToken)
    {
        if (query.Mode != SearchMode.Keyword && (query.Vector is null || query.VectorModel is null))
        {
            throw new ArgumentException("Semantic and hybrid search need the query vector.", nameof(query));
        }

        using var lease = await ReadLeaseAsync(cancellationToken);
        if (lease is null)
        {
            return new StoreSearchResult([], 0, query.WithFacets ? Facets([]) : null);
        }

        var index = lease.Index;
        var filter = ZvecFilter.For(query.Filter, index.Catalog);
        if (filter == ZvecFilter.False)
        {
            return new StoreSearchResult([], 0, query.WithFacets ? Facets([]) : null);
        }

        return query.Mode == SearchMode.Keyword ? Keyword(index, query, filter) : Fused(index, query, filter);
    }

    /// <summary>A matching document: its head row and rank.</summary>
    private sealed record Match(ZvecHit Head, double Rank);

    private StoreSearchResult Keyword(ZvecIndex index, StoreSearchQuery query, string filter)
    {
        var text = query.Text is null ? null : ZvecFullText.From(query.Text, index.Catalog.Prefix);
        var matches = text is null
            ? index.Collection.Text(Heading, AllToken, Heads(filter), options.Value.KeywordCandidates, HeadOutputs).Select(h => new Match(h, 0)).ToList()
            : KeywordMatches(index, text, filter, options.Value.KeywordCandidates);
        var ordered = text is null
            ? matches.OrderByDescending(m => m.Head.Number(UpdatedAt)).ThenBy(m => Guid.Parse(m.Head.Pk)).ToList()
            : matches.OrderByDescending(m => m.Rank).ThenBy(m => Guid.Parse(m.Head.Pk)).ToList();
        var page = ordered.Skip(query.Skip).Take(query.Top).ToList();
        string[] matchedBy = text is null ? [] : [SearchMatch.Keyword];
        var passages = BestPassages(index, text, page.Select(m => m.Head.Pk).ToList(), new Dictionary<string, Similar>());
        var hits = page.Select(m => Hit(m.Head, m.Rank, matchedBy, passages.GetValueOrDefault(m.Head.Pk))).ToList();
        return new StoreSearchResult(hits, ordered.Count, query.WithFacets ? Facets(ordered.Select(m => m.Head)) : null);
    }

    /// <summary>
    /// Documents whose text matches: a document matches when all of the query matches its content (in any language
    /// column), ranked by the best content score plus title and keyword matches; prefix groups first narrow the
    /// documents through the trigram column.
    /// </summary>
    private static List<Match> KeywordMatches(ZvecIndex index, ZvecFullText text, string filter, int limit)
    {
        var collection = index.Collection;
        var heads = Heads(filter);
        if (text.Prefixes.Count > 0)
        {
            HashSet<string>? candidates = null;
            foreach (var prefix in text.Prefixes)
            {
                var found = collection.Text(Prefix, prefix, heads, ZvecCollection.MaxTopK, []).Select(h => h.Pk).ToHashSet(StringComparer.Ordinal);
                candidates = candidates is null ? found : [.. candidates.Intersect(found)];
            }

            if (candidates!.Count == 0)
            {
                return [];
            }

            heads = ZvecFilter.And([heads, ZvecFilter.In(DocumentId, candidates)]);
        }

        var matches = new Dictionary<string, Match>(StringComparer.Ordinal);
        if (text.Text is null)
        {
            foreach (var hit in collection.Text(Heading, AllToken, heads, limit, HeadOutputs))
            {
                matches[hit.Pk] = new Match(hit, 0);
            }

            if (text.Excluded is { } excluded && matches.Count > 0)
            {
                foreach (var hit in collection.Text(Content, excluded, ZvecFilter.And([heads, ZvecFilter.In(DocumentId, matches.Keys)]), ZvecCollection.MaxTopK, []))
                {
                    matches.Remove(hit.Pk);
                }
            }

            return [.. matches.Values];
        }

        foreach (var column in index.Catalog.Languages.Select(ContentIn).Prepend(Content))
        {
            foreach (var hit in collection.Text(column, text.Text, heads, limit, HeadOutputs))
            {
                if (!matches.TryGetValue(hit.Pk, out var current) || current.Rank < hit.Score)
                {
                    matches[hit.Pk] = new Match(hit, hit.Score);
                }
            }
        }

        if (matches.Count > 0)
        {
            var matched = ZvecFilter.And([heads, ZvecFilter.In(DocumentId, matches.Keys)]);
            foreach (var hit in collection.Text(Heading, text.Text, matched, matches.Count, []))
            {
                matches[hit.Pk] = matches[hit.Pk] with { Rank = matches[hit.Pk].Rank + (HeadingWeight * hit.Score) };
            }
        }

        return [.. matches.Values];
    }

    /// <summary>A semantic match: the document, its most similar passage and the similarity (cosine).</summary>
    private sealed record Similar(string DocumentId, int? Page, string? Text, double Similarity);

    /// <summary>
    /// Semantic or hybrid: the best <see cref="StoreSearchQuery.CandidateLimit"/> documents of each side, fused by
    /// reciprocal rank; count and facets cover the fused candidates.
    /// </summary>
    private StoreSearchResult Fused(ZvecIndex index, StoreSearchQuery query, string filter)
    {
        var limit = Math.Max(1, query.CandidateLimit);
        var text = query.Text is null ? null : ZvecFullText.From(query.Text, index.Catalog.Prefix);
        var keyword = query.Mode == SearchMode.Hybrid && text is not null
            ? KeywordMatches(index, text, filter, options.Value.KeywordCandidates).OrderByDescending(m => m.Rank).ThenBy(m => Guid.Parse(m.Head.Pk)).Take(limit).Select(m => m.Head.Pk).ToList()
            : [];
        var semantic = SemanticMatches(index, query, text, filter, limit);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        void Fuse(IEnumerable<string> ids)
        {
            foreach (var (id, rank) in ids.Select((id, i) => (id, i)))
            {
                scores[id] = scores.GetValueOrDefault(id) + (1.0 / (SearchMatch.FusionK + rank + 1));
            }
        }

        Fuse(keyword);
        Fuse(semantic.Select(m => m.DocumentId));
        var ordered = scores.OrderByDescending(s => s.Value).ThenBy(s => Guid.Parse(s.Key)).ToList();
        var heads = index.Collection.Fetch(ordered.Select(s => s.Key).ToList(), HeadOutputs).ToDictionary(h => h.Pk, StringComparer.Ordinal);
        var page = ordered.Skip(query.Skip).Take(query.Top).Where(s => heads.ContainsKey(s.Key)).ToList();
        var keywordIds = keyword.ToHashSet(StringComparer.Ordinal);
        var semanticIds = semantic.ToDictionary(m => m.DocumentId, StringComparer.Ordinal);
        var passages = BestPassages(index, text, page.Where(s => keywordIds.Contains(s.Key)).Select(s => s.Key).ToList(), semanticIds);
        var hits = page.Select(s =>
        {
            string[] matchedBy = keywordIds.Contains(s.Key) && semanticIds.ContainsKey(s.Key) ? [SearchMatch.Keyword, SearchMatch.Semantic]
                : keywordIds.Contains(s.Key) ? [SearchMatch.Keyword] : [SearchMatch.Semantic];
            return Hit(heads[s.Key], s.Value, matchedBy, passages.GetValueOrDefault(s.Key));
        }).ToList();
        return new StoreSearchResult(hits, ordered.Count, query.WithFacets ? Facets(heads.Values) : null);
    }

    /// <summary>Documents with a passage similar to the query vector, best first, without documents containing an excluded word.</summary>
    private List<Similar> SemanticMatches(ZvecIndex index, StoreSearchQuery query, ZvecFullText? text, string filter, int limit)
    {
        if (index.Vectors?.Key != query.VectorModel)
        {
            return [];
        }

        var best = new Dictionary<string, Similar>(StringComparer.Ordinal);
        var passages = ZvecFilter.And([ZvecFilter.Equal(Kind, PassageKind), filter]);
        foreach (var hit in index.Collection.Nearest(Embedding, query.Vector!.Value.Span, passages, options.Value.PassageCandidates, PassageOutputs))
        {
            // zvec returns the cosine distance (0: same direction).
            var similarity = 1 - hit.Score;
            var document = hit.Text(DocumentId)!;
            if (similarity >= query.MinSimilarity && (!best.TryGetValue(document, out var current) || current.Similarity < similarity))
            {
                best[document] = new Similar(document, (int?)hit.Number(Page), hit.Text(Text), similarity);
            }
        }

        if (text?.Excluded is { } excluded && best.Count > 0)
        {
            foreach (var hit in index.Collection.Text(Content, excluded, Heads(ZvecFilter.In(DocumentId, best.Keys)), ZvecCollection.MaxTopK, []))
            {
                best.Remove(hit.Pk);
            }
        }

        return [.. best.Values.OrderByDescending(m => m.Similarity).ThenBy(m => Guid.Parse(m.DocumentId)).Take(limit)];
    }

    /// <summary>
    /// The passage each hit shows: for keyword matches the passage with the best full-text score, else the most
    /// similar one, else the document's first passage (SRC-09).
    /// </summary>
    private static Dictionary<string, (int? Page, string? Text)> BestPassages(
        ZvecIndex index, ZvecFullText? text, List<string> keywordIds, Dictionary<string, Similar> semantic)
    {
        var result = new Dictionary<string, (int? Page, string? Text)>(StringComparer.Ordinal);
        if (text?.Text is { } query && keywordIds.Count > 0)
        {
            var filter = ZvecFilter.And([ZvecFilter.Equal(Kind, PassageKind), ZvecFilter.In(DocumentId, keywordIds)]);
            var hits = index.Collection.Text(Content, query, filter, ZvecCollection.MaxTopK, PassageOutputs);
            foreach (var best in hits.GroupBy(h => h.Text(DocumentId)!).Select(g => g.OrderByDescending(h => h.Score).ThenBy(h => h.Number(Ordinal)).First()))
            {
                result[best.Text(DocumentId)!] = ((int?)best.Number(Page), best.Text(Text));
            }
        }

        foreach (var (id, match) in semantic)
        {
            result.TryAdd(id, (match.Page, match.Text));
        }

        var missing = keywordIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var first = ZvecFilter.And([ZvecFilter.Equal(Kind, PassageKind), $"{Ordinal} = 0", ZvecFilter.In(DocumentId, missing)]);
            foreach (var hit in index.Collection.Text(Heading, AllToken, first, missing.Count, PassageOutputs))
            {
                result.TryAdd(hit.Text(DocumentId)!, ((int?)hit.Number(Page), hit.Text(Text)));
            }
        }

        return result;
    }

    private static StoreHit Hit(ZvecHit head, double rank, string[] matchedBy, (int? Page, string? Text) passage) =>
        new(Guid.Parse(head.Pk), head.Text(SourceType) ?? string.Empty, ParseId(head.Text(WorkspaceId)) ?? Guid.Empty, ParseId(head.Text(ContainerId)),
            ParseId(head.Text(ContentTypeId)), head.Text(Title) ?? string.Empty, rank, ParseId(head.Text(CreatedBy)),
            new DateTimeOffset((long)(head.Number(UpdatedAt) ?? 0), TimeSpan.Zero))
        {
            Page = passage.Page,
            Text = passage.Text,
            MatchedBy = matchedBy,
        };

    private static Guid? ParseId(string? value) => Guid.TryParse(value, out var id) ? id : null;

    private static SearchFacets Facets(IEnumerable<ZvecHit> heads)
    {
        var list = heads.ToList();
        static List<FacetValue> Count(IEnumerable<Guid> values) =>
            [.. values.GroupBy(v => v).Select(g => new FacetValue(g.Key, g.Count())).OrderByDescending(v => v.Count).ThenBy(v => v.Value).Take(FacetSize)];

        return new SearchFacets(
            Count(list.Select(h => ParseId(h.Text(WorkspaceId))).OfType<Guid>()),
            Count(list.Select(h => ParseId(h.Text(ContainerId))).OfType<Guid>()),
            Count(list.Select(h => ParseId(h.Text(ContentTypeId))).OfType<Guid>()),
            Count(list.SelectMany(h => h.Texts(TermIds)).Select(ParseId).OfType<Guid>()));
    }
}
