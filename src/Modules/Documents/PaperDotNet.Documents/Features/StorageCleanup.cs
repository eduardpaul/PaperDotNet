using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>Removes the file versions of permanently deleted items (their content is then collected by the job).</summary>
internal sealed class PurgedItemFiles(DocumentsDbContext db) : IEventSubscriber<ItemPurged>
{
    public Task HandleAsync(ItemPurged integrationEvent, CancellationToken cancellationToken) =>
        db.FileVersions.Where(v => v.ItemId == integrationEvent.ItemId).ExecuteDeleteAsync(cancellationToken);
}

/// <summary>
/// Deletes stored content no file version refers to any more (DOC-11). Content used by an upload in
/// the last hour is kept, so an upload in progress never loses its blob.
/// </summary>
internal sealed class StoredFileCleanupJob(DocumentsDbContext db, IBlobStore blobs, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "documents.stored-file-cleanup";
    public const string Schedule = "17 * * * *";
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - GracePeriod;
        var orphans = await db.StoredFiles
            .Where(f => f.LastUsedAt < cutoff && !db.FileVersions.Any(v => v.StoredFileId == f.Id))
            .Take(500)
            .ToListAsync(cancellationToken);
        foreach (var file in orphans)
        {
            await blobs.DeleteAsync(file.BlobKey, cancellationToken);
            db.StoredFiles.Remove(file);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
