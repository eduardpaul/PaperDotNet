using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Dav.Data;
using PaperDotNet.Jobs.Contracts;

namespace PaperDotNet.Dav.Features;

/// <summary><c>dav.cleanup</c> (hourly, per tenant): removes expired locks and temporary files with their content.</summary>
internal sealed class DavCleanupJob(DavDbContext db, IBlobStore blobs, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "dav.cleanup";
    public const string Schedule = "17 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.Locks.Where(l => l.Expiration <= now).ExecuteDeleteAsync(cancellationToken);
        var expired = await db.TransientFiles.Where(f => f.ExpiresAt <= now).ToListAsync(cancellationToken);
        foreach (var file in expired)
        {
            if (file.BlobKey is { } key)
            {
                await blobs.DeleteAsync(key, cancellationToken);
            }
        }

        db.TransientFiles.RemoveRange(expired);
        await db.SaveChangesAsync(cancellationToken);
    }
}
