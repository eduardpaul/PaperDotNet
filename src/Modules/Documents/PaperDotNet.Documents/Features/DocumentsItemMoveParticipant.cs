using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Documents.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;

namespace PaperDotNet.Documents.Features;

/// <summary>Keeps item location metadata current in the identity-preserving move transaction.</summary>
internal sealed class DocumentsItemMoveParticipant(DocumentsDbContext db) : IItemMoveParticipant
{
    public Task MoveAsync(ItemMove move, DbTransaction transaction, CancellationToken cancellationToken) =>
        SharedTransaction.RunAsync(db, transaction, async ct =>
        {
            await db.FileVersions.Where(e => e.ItemId == move.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ListId, move.ListId)
                    .SetProperty(e => e.WorkspaceId, move.WorkspaceId), ct);
        }, cancellationToken);
}
