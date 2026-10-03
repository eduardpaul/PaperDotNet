using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Documents.Contracts;
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
            foreach (var candidate in await db.Candidates.Where(c => c.ItemId == move.ItemId).ToListAsync(ct))
            {
                var source = JsonSerializer.Deserialize<DocumentFile>(candidate.SourceJson)!;
                candidate.SourceJson = JsonSerializer.Serialize(source with { WorkspaceId = move.WorkspaceId, ListId = move.ListId });
            }

            await db.SaveChangesAsync(ct);
        }, cancellationToken);
}
