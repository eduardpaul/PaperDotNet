using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Data;

namespace PaperDotNet.Lists.Features;

/// <summary>Removes the permission entries of a deleted user or group (IAM-14).</summary>
internal sealed class PrincipalDeletedSubscriber(ListsDbContext db) : IEventSubscriber<PrincipalDeleted>
{
    public async Task HandleAsync(PrincipalDeleted integrationEvent, CancellationToken cancellationToken)
    {
        var type = integrationEvent.IsGroup ? AclPrincipalType.Group : AclPrincipalType.User;
        var entries = await db.AclEntries
            .Where(e => e.PrincipalType == type && e.PrincipalId == integrationEvent.PrincipalId)
            .ToListAsync(cancellationToken);
        if (entries.Count > 0)
        {
            // Tracked removal: delta sync records a reset for the affected lists.
            db.AclEntries.RemoveRange(entries);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
