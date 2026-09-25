using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Data;

namespace PaperDotNet.Lists.Features;

/// <summary>Removes the permission grants of a deleted user or group (IAM-14).</summary>
internal sealed class PrincipalDeletedSubscriber(ListsDbContext db) : IEventSubscriber<PrincipalDeleted>
{
    public async Task HandleAsync(PrincipalDeleted integrationEvent, CancellationToken cancellationToken)
    {
        var type = integrationEvent.IsGroup ? PrincipalType.Group : PrincipalType.User;
        var grants = await db.Grants
            .Where(g => g.PrincipalType == type && g.PrincipalId == integrationEvent.PrincipalId)
            .ToListAsync(cancellationToken);
        if (grants.Count > 0)
        {
            // Tracked removal: delta sync records a reset for the affected lists.
            db.Grants.RemoveRange(grants);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
