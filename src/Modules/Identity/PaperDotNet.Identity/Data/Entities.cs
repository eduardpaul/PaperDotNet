using Microsoft.AspNetCore.Identity;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Identity.Data;

public sealed class User : IdentityUser<Guid>, ITenantOwned, IAuditable
{
    public Guid TenantId { get; set; }

    public string? DisplayName { get; set; }

    public bool IsDisabled { get; set; }

    /// <summary>Identity of an OAuth client application (client credentials); cannot sign in interactively.</summary>
    public bool IsServiceAccount { get; set; }

    /// <summary>When the user was deleted (IAM-14): the row stays so references resolve, but it is anonymized.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

public sealed class Group : ITenantOwned, IAuditable, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Who manages the members: people in PaperDotNet, or an authenticating reverse proxy (ADR-0044).</summary>
    public GroupSource Source { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

public enum GroupSource
{
    /// <summary>Members are added and removed in PaperDotNet.</summary>
    Local = 0,

    /// <summary>Created by the reverse proxy; with group sync, members follow the proxy's groups at each sign-in.</summary>
    Proxy = 1,
}

public sealed class GroupMember : ITenantOwned
{
    public Guid GroupId { get; set; }

    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>A group inside a group (ADR-0035): the members of <see cref="MemberGroupId"/> are members of <see cref="GroupId"/> too.</summary>
public sealed class GroupNesting : ITenantOwned
{
    public Guid GroupId { get; set; }

    public Guid MemberGroupId { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>
/// Every group and every group it is inside of, directly or through other groups, including itself (a closure
/// table). Derived from <see cref="GroupNesting"/>: <see cref="IdentityDbContext"/> keeps it current on every save, so
/// "the groups of a user" is one join.
/// </summary>
[NotAudited]
public sealed class GroupClosure : ITenantOwned
{
    public Guid GroupId { get; set; }

    /// <summary>A group that contains <see cref="GroupId"/> (or the group itself).</summary>
    public Guid AncestorId { get; set; }

    public Guid TenantId { get; set; }
}

public sealed class Role : ITenantOwned, IAuditable, IVersioned
{
    public const string Administrator = "Administrator";
    public const string Member = "Member";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public bool IsBuiltIn { get; set; }

    /// <summary>Grants every scope in the catalog, including scopes added later (built-in Administrator).</summary>
    public bool GrantsAllScopes { get; set; }

    public List<string> Scopes { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

public enum PrincipalType
{
    User = 0,
    Group = 1,
}

public sealed class RoleAssignment : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RoleId { get; set; }

    public Guid PrincipalId { get; set; }

    public PrincipalType PrincipalType { get; set; }
}

/// <summary>
/// Personal access token. Platform-level lookup table (queried by hash before
/// the tenant is known), so it carries its tenant explicitly instead of being
/// tenant-filtered.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string TenantIdentifier { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    /// <summary>First characters of the token, shown to identify it.</summary>
    public required string Prefix { get; set; }

    public required byte[] Hash { get; set; }

    public List<string> Scopes { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Preferences of a user (PLT-17) or, with <see cref="UserId"/> <see cref="Guid.Empty"/>, the organization's
/// defaults (PLT-18). Null values are inherited.
/// </summary>
public sealed class Preferences : ITenantOwned, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public string? Language { get; set; }

    public string? TimeZone { get; set; }

    public string? DateFormat { get; set; }

    public string? TimeFormat { get; set; }

    public string? NumberFormat { get; set; }

    public string? Theme { get; set; }

    public string? DocumentLanguages { get; set; }

    public uint Version { get; set; }
}
