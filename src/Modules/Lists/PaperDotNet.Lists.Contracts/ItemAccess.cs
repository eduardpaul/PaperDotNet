using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Contracts;

/// <summary>
/// Item permissions for other modules (ADR-0035). Every item belongs to a permission scope (its list, or the nearest
/// folder or item with unique permissions); a scope's access list names users, groups and workspace roles.
/// </summary>
public interface IItemAccess
{
    /// <summary>
    /// The current user's principals: the user, their groups and their workspace roles
    /// (<see cref="WorkspaceRolePrincipals"/>). Cached per user; empty without a signed-in user.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetPrincipalsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The permission scopes the current user reaches in the given lists (every list of the tenant when null), with
    /// the highest level any of their principals has. A scope that is not returned gives no access.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, WorkspaceAccessLevel>> GetScopesAsync(IReadOnlyCollection<Guid>? listIds, CancellationToken cancellationToken);
}
