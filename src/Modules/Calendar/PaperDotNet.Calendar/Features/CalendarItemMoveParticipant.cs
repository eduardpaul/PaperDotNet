using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;

namespace PaperDotNet.Calendar.Features;

/// <summary>Keeps item location metadata current in the identity-preserving move transaction.</summary>
internal sealed class CalendarItemMoveParticipant(CalendarDbContext db) : IItemMoveParticipant
{
    public Task MoveAsync(ItemMove move, DbTransaction transaction, CancellationToken cancellationToken) =>
        SharedTransaction.RunAsync(db, transaction, async ct =>
        {
            await db.Recurrences.Where(e => e.ItemId == move.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ListId, move.ListId)
                    .SetProperty(e => e.WorkspaceId, move.WorkspaceId), ct);
            await db.Sources.Where(e => e.ItemId == move.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ListId, move.ListId), ct);
        }, cancellationToken);
}
