using Microsoft.EntityFrameworkCore;
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

        var ids = documents.Select(d => d.Id).ToList();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Principals.Where(p => ids.Contains(p.DocumentId)).ExecuteDeleteAsync(cancellationToken);
        await db.Tags.Where(t => ids.Contains(t.DocumentId)).ExecuteDeleteAsync(cancellationToken);
        var existing = await db.Documents.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);
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
            document.Body = data.Body.Length > MaxBodyLength ? data.Body[..MaxBodyLength] : data.Body;
            document.Keywords = data.Keywords.Length > MaxBodyLength ? data.Keywords[..MaxBodyLength] : data.Keywords;
            document.Language = FullTextLanguages.All.Contains(data.Language ?? string.Empty) ? data.Language : null;
            document.CreatedBy = data.CreatedBy;
            document.UpdatedAt = data.UpdatedAt;
            db.Principals.AddRange(data.Principals.Distinct(StringComparer.Ordinal).Select(p => new SearchPrincipal { DocumentId = data.Id, Principal = p }));
            db.Tags.AddRange(data.TermIds.Distinct().Select(t => new SearchTag { DocumentId = data.Id, TermId = t }));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
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
        await db.Principals.Where(p => documents.Any(d => d.Id == p.DocumentId)).ExecuteDeleteAsync(ct);
        await db.Tags.Where(t => documents.Any(d => d.Id == t.DocumentId)).ExecuteDeleteAsync(ct);
        await documents.ExecuteDeleteAsync(ct);
    }
}
