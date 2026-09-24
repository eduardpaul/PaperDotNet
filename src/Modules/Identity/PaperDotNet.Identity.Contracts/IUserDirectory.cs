namespace PaperDotNet.Identity.Contracts;

public sealed record NewUser(
    string UserName,
    string Password,
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

    /// <summary>Creates a user in the current tenant with the Member role (and Administrator if requested).</summary>
    Task<Guid> CreateUserAsync(NewUser user, CancellationToken cancellationToken);
}

public sealed class UserCreationException(IReadOnlyList<string> errors)
    : Exception("User could not be created: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
