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
    /// <summary>
    /// Which of <paramref name="itemIds"/> exist (not deleted, not folders) and the current user can read: for results of
    /// derived data (a search page, a page of runs) while it catches up with deletes or permission changes. Bounded by
    /// the ids given, never the user's whole readable set.
    /// </summary>
    Task<IReadOnlySet<Guid>> FilterReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>
    /// The permission scopes the current user reaches in the given lists (every list of the tenant when null), with
    /// the highest level any of their principals has. A scope that is not returned gives no access.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, WorkspaceAccessLevel>> GetScopesAsync(IReadOnlyCollection<Guid>? listIds, CancellationToken cancellationToken);
}
