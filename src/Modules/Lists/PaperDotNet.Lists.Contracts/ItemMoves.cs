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

/// <summary>A version-checked batch participant; Recycle=false validates and locks without deleting.</summary>
public sealed record ItemRecycleTarget(Guid WorkspaceId, Guid ListId, Guid ItemId, uint Version, bool Recycle, Guid? WorkflowRunId = null);

/// <summary>Validates expected versions and recycles marked items in the caller's transaction, without publishing live changes.</summary>
public interface IItemBatchRecycle
{
    Task RecycleAsync(IReadOnlyList<ItemRecycleTarget> items, DbTransaction transaction, CancellationToken cancellationToken);
    Task AnnounceAsync(IReadOnlyList<ItemRecycleTarget> items, CancellationToken cancellationToken);
}
