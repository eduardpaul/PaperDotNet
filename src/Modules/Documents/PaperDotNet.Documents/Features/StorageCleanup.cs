using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>
/// Removes the file versions of permanently deleted items, in the background (a Wolverine handler generated ahead of
/// time); their content is then collected by <see cref="StoredFileCleanupJob"/>. Idempotent.
/// </summary>
public static class PurgedFilesSubscriber
{
    public static async Task Handle(ItemPurged e, DocumentsDbContext db, CancellationToken cancellationToken)
    {
        var versions = await DocumentQueries.VersionsOfItemAsync(db, e.TenantId, e.ItemId, cancellationToken);
        if (versions.Count > 0)
        {
            db.FileVersions.RemoveRange(versions);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Deletes stored content no file version refers to any more (DOC-11), with its page texts. Content
/// used by an upload in the last hour is kept, so an upload in progress never loses its blob.
/// </summary>
internal sealed class StoredFileCleanupJob(DocumentsDbContext db, IBlobStore blobs, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "documents.storedFileCleanup";
    public const string Schedule = "17 * * * *";
    private const int Batch = 500;
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(1);

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var cutoff = (time.GetUtcNow() - GracePeriod).ToUnixTimeMilliseconds();
        while (true)
        {
            var orphans = await DocumentQueries.OrphansAsync(db, tenantId, cutoff, Batch, cancellationToken);
            if (orphans.Count == 0)
            {
                return;
            }

            foreach (var file in orphans)
            {
                await blobs.DeleteAsync(file.BlobKey, cancellationToken);
                db.Pages.RemoveRange(await DocumentQueries.PagesOfAsync(db, tenantId, file.Id, cancellationToken));
                db.StoredFiles.Remove(file);
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }
}
