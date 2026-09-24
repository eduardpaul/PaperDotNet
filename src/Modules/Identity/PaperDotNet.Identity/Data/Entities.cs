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

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public uint Version { get; set; }
}

public sealed class GroupMember : ITenantOwned
{
    public Guid GroupId { get; set; }

    public Guid UserId { get; set; }

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
