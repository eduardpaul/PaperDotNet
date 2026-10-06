using PaperDotNet.Abstractions;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Contracts;

/// <summary>
/// Item permissions for other modules (ADR-0035). Every item belongs to a permission scope (its list, or the nearest
/// folder or item with unique permissions); a scope's access list names users, groups and workspace roles.
/// </summary>
/// <remarks>
/// <see cref="IPrincipalSet.GetPrincipalsAsync"/> gives the current user's principals: the user, their groups and
/// their workspace roles (<see cref="WorkspaceRolePrincipals"/>); cached per user, empty without a signed-in user.
/// </remarks>
public interface IItemAccess : IPrincipalSet
{
    /// <summary>Currently active readable items, for visibility while derived indexes catch up with deletes or scope moves.</summary>
    Task<IReadOnlyCollection<Guid>> GetReadableItemIdsAsync(Guid? workspaceId, CancellationToken cancellationToken);
    /// <summary>
    /// The permission scopes the current user reaches in the given lists (every list of the tenant when null), with
    /// the highest level any of their principals has. A scope that is not returned gives no access.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, WorkspaceAccessLevel>> GetScopesAsync(IReadOnlyCollection<Guid>? listIds, CancellationToken cancellationToken);
}
