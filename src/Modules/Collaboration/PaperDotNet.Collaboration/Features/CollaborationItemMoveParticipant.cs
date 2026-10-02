using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Collaboration.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;

namespace PaperDotNet.Collaboration.Features;

/// <summary>Keeps item location metadata current in the identity-preserving move transaction.</summary>
internal sealed class CollaborationItemMoveParticipant(CollaborationDbContext db) : IItemMoveParticipant
{
    public Task MoveAsync(ItemMove move, DbTransaction transaction, CancellationToken cancellationToken) =>
        SharedTransaction.RunAsync(db, transaction, async ct =>
        {
            await db.Comments.Where(e => e.ItemId == move.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ListId, move.ListId)
                    .SetProperty(e => e.WorkspaceId, move.WorkspaceId), ct);
            await db.Activity.Where(e => e.ItemId == move.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ListId, move.ListId)
                    .SetProperty(e => e.WorkspaceId, move.WorkspaceId), ct);
        }, cancellationToken);
}
