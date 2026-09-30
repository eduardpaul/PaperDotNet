using System.Security.Claims;

namespace PaperDotNet.Abstractions;

/// <summary>Claim types in PaperDotNet access tokens.</summary>
public static class PaperDotNetClaims
{
    public const string UserId = "sub";
    public const string TenantId = "tid";
    public const string UserName = "name";
    public const string Scope = "scope";

    /// <summary>Changes when the password changes or the user is disabled; refresh tokens with an old stamp fail.</summary>
    public const string SecurityStamp = "stamp";
}

/// <summary>The caller of the current request (or nobody, in background work).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    Guid? TenantId { get; }

    bool IsAuthenticated { get; }

    bool HasScope(string scope);
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? FindGuid(this ClaimsPrincipal principal, string type) =>
        Guid.TryParse(principal.FindFirst(type)?.Value, out var value) ? value : null;

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
