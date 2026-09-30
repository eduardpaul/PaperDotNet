using System.Security.Claims;

namespace PaperDotNet.Abstractions;

/// <summary>Claim types in PaperDotNet access tokens.</summary>
public static class PaperDotNetClaims
{
    public const string UserId = "sub";
    public const string TenantId = "tid";
    public const string UserName = "name";
    /// <summary>
    /// Scopes a token is limited to (an API token, or a token requested with <c>scope</c>). Without it, a token has
    /// the user's effective scopes, checked on every request (<see cref="IEffectiveScopeProvider"/>).
    /// </summary>
    public const string Scope = "scope";

    /// <summary>The id of the API token a request was authenticated with.</summary>
    public const string TokenId = "tkn";

    /// <summary>Changes when the password changes or the user is disabled; refresh tokens with an old stamp fail.</summary>
    public const string SecurityStamp = "stamp";
}

/// <summary>The caller of the current request (or nobody, in background work).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    Guid? TenantId { get; }

    bool IsAuthenticated { get; }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? FindGuid(this ClaimsPrincipal principal, string type) =>
        Guid.TryParse(principal.FindFirst(type)?.Value, out var value) ? value : null;

    /// <summary>Whether the token is limited to some scopes (<see cref="PaperDotNetClaims.Scope"/>).</summary>
    public static bool IsScopeLimited(this ClaimsPrincipal principal) => principal.HasClaim(c => c.Type == PaperDotNetClaims.Scope);

    /// <summary>Whether the token's scope limitation includes <paramref name="scope"/>.</summary>
    public static bool HasScope(this ClaimsPrincipal principal, string scope)
    {
        foreach (var claim in principal.FindAll(PaperDotNetClaims.Scope))
        {
            foreach (var range in claim.Value.AsSpan().Split(' '))
            {
                if (claim.Value.AsSpan()[range].Equals(scope, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
