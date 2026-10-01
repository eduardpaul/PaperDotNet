using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;

namespace PaperDotNet.Search.Features;

/// <summary>Writes the index: documents and passages with EF Core, bulk removals and scope moves with provider SQL.</summary>
internal sealed class SearchIndex(SearchDbContext db, ISearchQueries queries) : ISearchIndex
{
    /// <summary>Body text beyond this is not indexed (keeps rows and full-text indexes reasonable).</summary>
    public const int MaxBodyLength = 200_000;

    public async Task UpsertAsync(Guid tenantId, IReadOnlyCollection<SearchDocumentData> documents, CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return;
        }

        // The same item can be indexed from two places at once (e.g. two events): the loser of the race hits a key
        // conflict and simply writes again on top of the winner.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WriteAsync(tenantId, documents, cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task WriteAsync(Guid tenantId, IReadOnlyCollection<SearchDocumentData> documents, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var data in documents.DistinctBy(d => d.Id))
        {
            var context = db;
            var tenant = tenantId;
            var id = data.Id;
            var ct = cancellationToken;
            var document = await context.Documents.FirstOrDefaultAsync(d => d.TenantId == tenant && d.Id == id, ct);
            if (document is null)
            {
                document = new SearchDocument { Id = data.Id, TenantId = tenantId };
                context.Documents.Add(document);
            }

            document.SourceType = data.SourceType;
            document.WorkspaceId = data.WorkspaceId;
            document.ContainerId = data.ContainerId;
            document.ContentTypeId = data.ContentTypeId;
            document.ScopeId = data.ScopeId;
            document.Title = Truncate(data.Title, 1024);
            var body = data.Pages.Count == 0 ? data.Body : $"{data.Body}\n{string.Join('\n', data.Pages)}";
            document.Body = Truncate(body, MaxBodyLength);
            document.Keywords = Truncate(data.Keywords, MaxBodyLength);
            document.Language = data.Language is { Length: <= 20 } language ? language : null;
            document.CreatedBy = data.CreatedBy;
            document.UpdatedAt = data.UpdatedAt.ToUnixTimeMilliseconds();

            context.Tags.RemoveRange(await context.Tags.Where(t => t.TenantId == tenant && t.DocumentId == id).ToListAsync(ct));
            context.Tags.AddRange(data.TermIds.Distinct().Select(t => new SearchTag { DocumentId = data.Id, TermId = t, TenantId = tenantId }));
            var passages = await context.Passages.Where(p => p.TenantId == tenant && p.DocumentId == id).ToListAsync(ct);
            UpdatePassages(tenantId, data, document.Title, passages);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    /// <summary>Replaces the document's passages; a passage whose text is unchanged keeps its row (moved to its new position).</summary>
    private void UpdatePassages(Guid tenantId, SearchDocumentData data, string title, List<SearchPassage> current)
    {
        var reusable = current.GroupBy(p => p.ContentHash).ToDictionary(g => g.Key, g => new Queue<SearchPassage>(g));
        foreach (var (passage, ordinal) in Passages.Split(data).Select((p, i) => (p, i)))
        {
            var hash = Passages.Hash(Passages.EmbeddingInput(title, passage.Text));
            if (reusable.TryGetValue(hash, out var queue) && queue.TryDequeue(out var kept))
            {
                kept.Ordinal = ordinal;
                kept.Page = passage.Page;
                continue;
            }

            db.Passages.Add(new SearchPassage
            {
                Id = Ids.New(),
                TenantId = tenantId,
                DocumentId = data.Id,
                Ordinal = ordinal,
                Page = passage.Page,
                Text = passage.Text,
                ContentHash = hash,
            });
        }

        db.Passages.RemoveRange(reusable.Values.SelectMany(q => q));
    }

    public Task DeleteAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? Task.CompletedTask : queries.DeleteAsync(tenantId, ids, null, null, cancellationToken);

    public async Task SetScopesAsync(Guid tenantId, IReadOnlyDictionary<Guid, Guid> scopes, CancellationToken cancellationToken)
    {
        foreach (var group in scopes.GroupBy(s => s.Value, s => s.Key))
        {
            await queries.SetScopeAsync(tenantId, group.Key, [.. group], cancellationToken);
        }
    }

    public Task DeleteContainerAsync(Guid tenantId, Guid containerId, CancellationToken cancellationToken) =>
        queries.DeleteAsync(tenantId, null, containerId, null, cancellationToken);

    public Task DeleteSourceAsync(Guid tenantId, string sourceType, CancellationToken cancellationToken) =>
        queries.DeleteAsync(tenantId, null, null, sourceType, cancellationToken);

    private static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;
}

/// <summary>Counts tag usage from the index (one row per document and term).</summary>
internal sealed class TermUsage(ISearchQueries queries) : ITermUsage
{
    public Task<IReadOnlyDictionary<Guid, int>> CountAsync(Guid tenantId, IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken) =>
        queries.CountTermsAsync(tenantId, [.. termIds.Distinct()], cancellationToken);
}
