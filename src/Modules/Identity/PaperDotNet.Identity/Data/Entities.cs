using System.ComponentModel.DataAnnotations;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Identity.Data;

// Entity classes are not sealed: EF Core's precompiled materializers test them for IInjectableService (ADR-0039).
#pragma warning disable CA1852

public class Tenant
{
    public Guid Id { get; set; }

    /// <summary>Stable name used to sign in (<c>tenant</c> in the token request).</summary>
    public string Identifier { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>A <see cref="TenantStatuses"/> value: users of a suspended tenant cannot sign in.</summary>
    public string Status { get; set; } = TenantStatuses.Active;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A custom host name mapped to a tenant (e.g. <c>dms.acme.com</c>): requests to it are in that tenant.</summary>
public class TenantHost
{
    /// <summary>Lower-case host name, unique across tenants.</summary>
    public string Host { get; set; } = "";

    public Guid TenantId { get; set; }
}

/// <summary>Stored values of <see cref="Tenant.Status"/> (strings, not an enum: ADR-0039).</summary>
public static class TenantStatuses
{
    public const string Active = "active";
    public const string Suspended = "suspended";
}

public class User : ITenantOwned, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string UserName { get; set; } = "";

    /// <summary>Upper-case invariant user name, unique per tenant.</summary>
    public string NormalizedUserName { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string? Email { get; set; }

    /// <summary>Empty when the user has no password (e.g. imported users, until an administrator sets one).</summary>
    public string PasswordHash { get; set; } = "";

    public bool IsDisabled { get; set; }

    /// <summary>The account of an OAuth client (client credentials): no password, no interactive sign-in.</summary>
    public bool IsServiceAccount { get; set; }

    /// <summary>When the user was deleted (IAM-14): the row stays so references resolve, but it is anonymized.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Changes with the password or when access ends; refresh tokens carry it.</summary>
    public string SecurityStamp { get; set; } = "";

    /// <summary>Failed sign-ins in a row; at <c>Users.MaxFailedSignIns</c> the account is locked for a while.</summary>
    public int AccessFailedCount { get; set; }

    public DateTimeOffset? LockoutEnd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

public class Group : ITenantOwned, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

public class GroupMember : ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid GroupId { get; set; }

    public Guid UserId { get; set; }
}

/// <summary>A group inside a group (ADR-0035): the members of <see cref="MemberGroupId"/> are members of <see cref="GroupId"/> too.</summary>
public class GroupNesting : ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid GroupId { get; set; }

    public Guid MemberGroupId { get; set; }
}

/// <summary>
/// Every group and every group it is inside of, directly or through other groups, including itself (a closure
/// table). Derived from <see cref="GroupNesting"/>: <see cref="IdentityDbContext"/> keeps it current on every save, so
/// "the groups of a user" is one join.
/// </summary>
public class GroupClosure : ITenantOwned, INotAudited
{
    public Guid TenantId { get; set; }

    public Guid GroupId { get; set; }

    /// <summary>A group that contains <see cref="GroupId"/> (or the group itself).</summary>
    public Guid AncestorId { get; set; }
}

/// <summary>An id and a name (a query projection).</summary>
public sealed record NamedPrincipal(Guid Id, string Name);

/// <summary>A nesting as a pair (a query projection; here because generated queries import only this namespace).</summary>
public sealed record NestingPair(Guid GroupId, Guid MemberGroupId);

public class Role : ITenantOwned, IVersioned
{
    public const string Administrator = "Administrator";
    public const string Member = "Member";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public bool IsBuiltIn { get; set; }

    /// <summary>Grants every scope in the catalog, including scopes added later (built-in Administrator).</summary>
    public bool GrantsAllScopes { get; set; }

    /// <summary>Scope names separated by spaces (as in OAuth).</summary>
    public string Scopes { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

/// <summary>Stored values of <see cref="RoleAssignment.PrincipalType"/>.</summary>
public static class PrincipalTypes
{
    public const string User = "user";
    public const string Group = "group";
}

public class RoleAssignment : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RoleId { get; set; }

    public Guid PrincipalId { get; set; }

    /// <summary>A <see cref="PrincipalTypes"/> value.</summary>
    public string PrincipalType { get; set; } = "";
}

/// <summary>
/// Personal access token (API-05), looked up by the hash of its secret before the tenant is known; the tenant is
/// stored with it.
/// </summary>
public class ApiToken : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>First characters of the token, shown to identify it.</summary>
    public string Prefix { get; set; } = "";

    public byte[] Hash { get; set; } = [];

    /// <summary>Scope names separated by spaces: the token can do no more than these (and than its user).</summary>
    public string Scopes { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Preferences of a user (PLT-17) or, with <see cref="UserId"/> <see cref="Guid.Empty"/>, the organization's
/// defaults (PLT-18). Null values are inherited.
/// </summary>
public class Preferences : ITenantOwned, IVersioned
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

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

#pragma warning restore CA1852

/// <summary>Space-separated scope lists, as stored on roles and API tokens.</summary>
internal static class ScopeList
{
    public static string[] Parse(string scopes) => scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string Format(IEnumerable<string> scopes) => string.Join(' ', scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}

/// <summary>Stored values of <see cref="OAuthClient.ClientType"/>.</summary>
public static class OAuthClientTypes
{
    /// <summary>Has a secret (server-side apps, services).</summary>
    public const string Confidential = "confidential";

    /// <summary>No secret (browser and native apps): authorization code with PKCE only.</summary>
    public const string Public = "public";
}

/// <summary>
/// An OAuth client registered in a tenant (IAM-02). Clients are trusted by the organization that registers them, so
/// there is no consent screen. <c>paperdotnet</c> is the built-in first-party client of every tenant.
/// </summary>
public class OAuthClient : ITenantOwned
{
    public const string FirstPartyClientId = "paperdotnet";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string ClientId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary><see cref="OAuthClientTypes"/>.</summary>
    public string ClientType { get; set; } = OAuthClientTypes.Public;

    /// <summary>SHA-256 (hex) of the secret of a confidential client (a random 256-bit value, so a fast hash is enough).</summary>
    public string? SecretHash { get; set; }

    /// <summary>Allowed grant types, space separated.</summary>
    public string GrantTypes { get; set; } = "";

    /// <summary>Scopes the client may request, space separated.</summary>
    public string Scopes { get; set; } = "";

    /// <summary>Exact redirect URIs, space separated.</summary>
    public string RedirectUris { get; set; } = "";

    /// <summary>Where <c>/connect/logout</c> may return to, space separated.</summary>
    public string PostLogoutRedirectUris { get; set; } = "";

    /// <summary>The account the client acts as with the client credentials grant.</summary>
    public Guid? ServiceUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An authorization code (single use, minutes long): stored as the hash of the code.</summary>
public class OAuthCode : ITenantOwned, INotAudited
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string CodeHash { get; set; } = "";

    public string ClientId { get; set; } = "";

    public Guid UserId { get; set; }

    public string RedirectUri { get; set; } = "";

    /// <summary>The PKCE challenge (S256).</summary>
    public string CodeChallenge { get; set; } = "";

    public string Scope { get; set; } = "";

    public string? Nonce { get; set; }

    /// <summary>The user's security stamp when the code was issued: a password change in between voids it.</summary>
    public string SecurityStamp { get; set; } = "";

    /// <summary>Unix milliseconds (compared in SQL).</summary>
    public long ExpiresAt { get; set; }
}

/// <summary>
/// The RSA key that signs identity tokens (published at <c>/.well-known/jwks</c>), shared by every server; the private
/// key is protected with Data Protection.
/// </summary>
public class ServerKey : INotAudited
{
    public Guid Id { get; set; }

    /// <summary><c>sig</c>.</summary>
    public string Use { get; set; } = "";

    public string ProtectedKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A passkey of a user (IAM-01, WebAuthn): the credential's public key and what the authenticator reported, checked by
/// ASP.NET Core Identity's passkey handler.
/// </summary>
public class UserPasskey : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The credential id, base64url.</summary>
    public string CredentialId { get; set; } = "";

    public string Name { get; set; } = "";

    public byte[] PublicKey { get; set; } = [];

    /// <summary>The authenticator's signature counter (a counter that goes back means a cloned key).</summary>
    public long SignCount { get; set; }

    /// <summary>Transports (<c>internal</c>, <c>usb</c>, …), space separated.</summary>
    public string Transports { get; set; } = "";

    public bool IsUserVerified { get; set; }

    public bool IsBackupEligible { get; set; }

    public bool IsBackedUp { get; set; }

    public byte[] AttestationObject { get; set; } = [];

    public byte[] ClientDataJson { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}
