using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>Keeps item location metadata current in the identity-preserving move transaction.</summary>
internal sealed class WorkflowsItemMoveParticipant(WorkflowsDbContext db) : IItemMoveParticipant
{
    public Task MoveAsync(ItemMove move, DbTransaction transaction, CancellationToken cancellationToken) =>
        SharedTransaction.RunAsync(db, transaction, async ct =>
        {
            await db.Runs.Where(e => e.ItemId == move.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ListId, move.ListId)
                    .SetProperty(e => e.WorkspaceId, move.WorkspaceId), ct);
            await db.Approvals.Where(e => e.ItemId == move.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ListId, move.ListId)
                    .SetProperty(e => e.WorkspaceId, move.WorkspaceId), ct);
        }, cancellationToken);
}
