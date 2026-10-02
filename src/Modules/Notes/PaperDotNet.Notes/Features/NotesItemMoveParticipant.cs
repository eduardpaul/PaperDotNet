using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notes.Data;
using PaperDotNet.Persistence;

namespace PaperDotNet.Notes.Features;

/// <summary>Keeps item location metadata current in the identity-preserving move transaction.</summary>
internal sealed class NotesItemMoveParticipant(NotesDbContext db) : IItemMoveParticipant
{
    public Task MoveAsync(ItemMove move, DbTransaction transaction, CancellationToken cancellationToken) =>
        SharedTransaction.RunAsync(db, transaction, async ct =>
        {
            await db.Notes.Where(e => e.ItemId == move.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ListId, move.ListId)
                    .SetProperty(e => e.WorkspaceId, move.WorkspaceId), ct);
            await db.Links.Where(e => e.SourceItemId == move.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.SourceListId, move.ListId)
                    .SetProperty(e => e.WorkspaceId, move.WorkspaceId), ct);
        }, cancellationToken);
}
