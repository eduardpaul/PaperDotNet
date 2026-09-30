namespace PaperDotNet.Identity.Contracts;

/// <summary>A user to create; without <paramref name="Password"/> the user signs in only after a reset (imports, PLT-15).</summary>
public sealed record NewUser(
    string UserName,
    string? Password,
    string? DisplayName = null,
    string? Email = null,
    bool Administrator = false);

/// <summary>
/// User lookups and provisioning for other modules, bootstrap and the CLI. Every call names its tenant (there is no
/// ambient tenant under Native AOT, ADR-0039).
/// </summary>
public interface IUserDirectory
{
    /// <summary>True when the user exists, is not deleted and is enabled in the tenant.</summary>
    Task<bool> IsActiveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    Task<bool> AnyUsersAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Groups the user belongs to, directly or through groups inside groups (for permission checks).</summary>
    Task<IReadOnlyList<Guid>> GetGroupIdsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    Task<bool> GroupExistsAsync(Guid tenantId, Guid groupId, CancellationToken cancellationToken);

    /// <summary>User names by id (unknown ids are left out), e.g. to reference users by name in templates.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Group names by id (unknown ids are left out).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetGroupNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken);

    /// <summary>Enabled members of a group, including the members of groups inside it.</summary>
    Task<IReadOnlyList<Guid>> GetGroupMembersAsync(Guid tenantId, Guid groupId, CancellationToken cancellationToken);

    /// <summary>The user with this user name in the tenant, if any.</summary>
    Task<Guid?> FindUserAsync(Guid tenantId, string userName, CancellationToken cancellationToken);

    /// <summary>The group with this name in the tenant, if any.</summary>
    Task<Guid?> FindGroupAsync(Guid tenantId, string name, CancellationToken cancellationToken);

    /// <summary>Creates a user in the tenant with the Member role (and Administrator if requested).</summary>
    Task<Guid> CreateUserAsync(Guid tenantId, NewUser user, CancellationToken cancellationToken);
}

public sealed class UserCreationException(IReadOnlyList<string> errors)
    : Exception("User could not be created: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Changes to the built-in roles of a tenant (e.g. when an extension is enabled).</summary>
public interface IRoleProvisioning
{
    /// <summary>Adds scopes to the built-in Member role (no-op for scopes it already has).</summary>
    Task GrantToMembersAsync(Guid tenantId, IReadOnlyCollection<string> scopes, CancellationToken cancellationToken);
}
