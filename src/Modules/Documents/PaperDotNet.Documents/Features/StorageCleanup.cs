using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>Removes the file versions of permanently deleted items (their content is then collected by the job).</summary>
internal sealed class PurgedItemFiles(DocumentsDbContext db) : IEventSubscriber<ItemPurged>
{
    public async Task HandleAsync(ItemPurged integrationEvent, CancellationToken cancellationToken)
    {
        await db.FileVersions.Where(v => v.ItemId == integrationEvent.ItemId).ExecuteDeleteAsync(cancellationToken);
        await db.Candidates.Where(c => c.ItemId == integrationEvent.ItemId && c.State == "pending")
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "abandoned"), cancellationToken);
    }
}

/// <summary>
/// Deletes stored content no file version refers to any more (DOC-11). Content used by an upload in
/// the last hour is kept, so an upload in progress never loses its blob.
/// </summary>
internal sealed class StoredFileCleanupJob(DocumentsDbContext db, IBlobStore blobs, TimeProvider time, IWorkflowDirectory workflows, IEnumerable<IStagedDocumentRetention> retention) : ITenantRecurringJob
{
    public const string Name = "documents.stored-file-cleanup";
    public const string Schedule = "17 * * * *";
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - GracePeriod;
        var pending = await db.Candidates.Where(c => c.State == "pending" && c.CreatedAt < cutoff).Take(500).ToListAsync(cancellationToken);
        foreach (var candidate in pending)
        {
            if (!await workflows.IsRunActiveAsync(candidate.RunId, cancellationToken))
            {
                candidate.State = "abandoned";
            }
        }

        foreach (var staged in await db.StagedFiles.Where(c => c.State != "released" && c.CreatedAt < cutoff).Take(500).ToListAsync(cancellationToken))
        {
            var owner = retention.SingleOrDefault(r => r.Owner == staged.Owner);
            if (owner is null || !await owner.IsRetainedAsync(staged.Id, cancellationToken)) staged.State = "released";
        }
        await db.SaveChangesAsync(cancellationToken);
        var orphans = await Orphans(cutoff).AsNoTracking().Take(500).ToListAsync(cancellationToken);
        foreach (var file in orphans)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Recheck references and hold the content row so an intake cannot reuse it while bytes are removed.
            // Concurrent cleanup delivery may find the row already gone; that is successful cleanup.
            if (await Orphans(cutoff).Where(f => f.Id == file.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.LastUsedAt, f => f.LastUsedAt), cancellationToken) != 1) continue;
            await blobs.DeleteAsync(file.BlobKey, cancellationToken);
            var maxPage = await db.Pages.Where(p => p.StoredFileId == file.Id).Select(p => (int?)p.PageNumber).MaxAsync(cancellationToken) ?? 1;
            for (var page = 1; page <= maxPage; page++)
            {
                foreach (var width in PageRenderer.Widths)
                {
                    await blobs.DeleteAsync(PageRenderer.Key(file, page, width), cancellationToken);
                }
            }

            await db.Pages.Where(p => p.StoredFileId == file.Id).ExecuteDeleteAsync(cancellationToken);
            await db.StoredFiles.Where(f => f.Id == file.Id).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private IQueryable<StoredFile> Orphans(DateTimeOffset cutoff) => db.StoredFiles
        .Where(f => f.LastUsedAt < cutoff && !db.FileVersions.Any(v => v.StoredFileId == f.Id)
            && !db.Candidates.Any(c => c.State == "pending" && (c.StoredFileId == f.Id || c.SourceStoredFileId == f.Id))
            && !db.StagedFiles.Any(c => c.State != "released" && (c.StoredFileId == f.Id
                || db.StagedReferences.Any(s => s.StagedFileId == c.Id && s.StoredFileId == f.Id))));
}
