namespace PaperDotNet.Identity.Contracts;

/// <summary>A user to create; without <paramref name="Password"/> the user signs in only after a reset (imports, PLT-15).</summary>
public sealed record NewUser(
    string UserName,
    string? Password,
    string? DisplayName = null,
    string? Email = null,
    bool Administrator = false);

/// <summary>User lookups and provisioning for other modules, bootstrap and the CLI.</summary>
public interface IUserDirectory
{
    /// <summary>True when the user exists and is enabled in the current tenant.</summary>
    Task<bool> IsActiveAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> AnyUsersAsync(CancellationToken cancellationToken);

    /// <summary>Groups the user belongs to (for permission checks).</summary>
    Task<IReadOnlyList<Guid>> GetGroupIdsAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> GroupExistsAsync(Guid groupId, CancellationToken cancellationToken);

    /// <summary>User names by id (unknown ids are left out), e.g. to reference users by name in templates.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Group names by id (unknown ids are left out).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetGroupNamesAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken);

    /// <summary>Enabled members of a group.</summary>
    Task<IReadOnlyList<Guid>> GetGroupMembersAsync(Guid groupId, CancellationToken cancellationToken);

    /// <summary>The user with this user name in the current tenant, if any.</summary>
    Task<Guid?> FindUserAsync(string userName, CancellationToken cancellationToken);

    /// <summary>The group with this name in the current tenant, if any.</summary>
    Task<Guid?> FindGroupAsync(string name, CancellationToken cancellationToken);

    /// <summary>Creates a user in the current tenant with the Member role (and Administrator if requested).</summary>
    Task<Guid> CreateUserAsync(NewUser user, CancellationToken cancellationToken);
}

public sealed class UserCreationException(IReadOnlyList<string> errors)
    : Exception("User could not be created: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Changes to the built-in roles of the current tenant (e.g. when an extension is enabled).</summary>
public interface IRoleProvisioning
{
    /// <summary>Adds scopes to the built-in Member role (no-op for scopes it already has).</summary>
    Task GrantToMembersAsync(IReadOnlyCollection<string> scopes, CancellationToken cancellationToken);
}
