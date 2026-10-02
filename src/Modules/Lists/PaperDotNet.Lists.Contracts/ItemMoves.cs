using System.Data.Common;

namespace PaperDotNet.Lists.Contracts;

/// <summary>An identity-preserving move. Participants update their location metadata in the same transaction.</summary>
public sealed record ItemMove(Guid ItemId, Guid FromWorkspaceId, Guid FromListId, Guid WorkspaceId, Guid ListId);

/// <summary>
/// Updates extension-owned location metadata during a move. Use the supplied transaction; do not commit it or
/// publish side effects. Throwing rolls back the entire move, including the item and all other participants.
/// </summary>
public interface IItemMoveParticipant
{
    Task MoveAsync(ItemMove move, DbTransaction transaction, CancellationToken cancellationToken);
}
