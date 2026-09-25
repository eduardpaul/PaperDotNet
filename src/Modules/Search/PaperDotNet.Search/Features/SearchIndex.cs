using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;

namespace PaperDotNet.Search.Features;

internal sealed class SearchIndex(SearchDbContext db) : ISearchIndex
{
    /// <summary>Body text beyond this is not indexed (keeps rows and FTS indexes reasonable).</summary>
    public const int MaxBodyLength = 200_000;

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
        await db.Principals.Where(p => ids.Contains(p.DocumentId)).ExecuteDeleteAsync(cancellationToken);
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
            document.Title = data.Title.Length > 1024 ? data.Title[..1024] : data.Title;
            var body = data.Pages.Count == 0 ? data.Body : $"{data.Body}\n{string.Join('\n', data.Pages)}";
            document.Body = body.Length > MaxBodyLength ? body[..MaxBodyLength] : body;
            document.Keywords = data.Keywords.Length > MaxBodyLength ? data.Keywords[..MaxBodyLength] : data.Keywords;
            document.Language = FullTextLanguages.All.Contains(data.Language ?? string.Empty) ? data.Language : null;
            document.CreatedBy = data.CreatedBy;
            document.UpdatedAt = data.UpdatedAt;
            db.Principals.AddRange(data.Principals.Distinct(StringComparer.Ordinal).Select(p => new SearchPrincipal { DocumentId = data.Id, Principal = p }));
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
        await db.Principals.Where(p => ids.Contains(p.DocumentId)).ExecuteDeleteAsync(cancellationToken);
        await db.Tags.Where(t => ids.Contains(t.DocumentId)).ExecuteDeleteAsync(cancellationToken);
        await db.Documents.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task DeleteContainerAsync(Guid containerId, CancellationToken cancellationToken) =>
        await DeleteWhereAsync(db.Documents.Where(d => d.ContainerId == containerId), cancellationToken);

    public async Task DeleteSourceAsync(string sourceType, CancellationToken cancellationToken) =>
        await DeleteWhereAsync(db.Documents.Where(d => d.SourceType == sourceType), cancellationToken);

    private async Task DeleteWhereAsync(IQueryable<SearchDocument> documents, CancellationToken ct)
    {
        await db.Passages.Where(p => documents.Any(d => d.Id == p.DocumentId)).ExecuteDeleteAsync(ct);
        await db.Principals.Where(p => documents.Any(d => d.Id == p.DocumentId)).ExecuteDeleteAsync(ct);
        await db.Tags.Where(t => documents.Any(d => d.Id == t.DocumentId)).ExecuteDeleteAsync(ct);
        await documents.ExecuteDeleteAsync(ct);
    }
}

/// <summary>Counts tag usage from the index (one row per document and term).</summary>
internal sealed class TermUsage(SearchDbContext db) : ITermUsage
{
    private const int ChunkSize = 500;

    public async Task<IReadOnlyDictionary<Guid, int>> CountAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var chunk in termIds.Distinct().Chunk(ChunkSize))
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
}
