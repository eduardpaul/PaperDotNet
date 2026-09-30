using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed class AuthOptions
{
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);
}

/// <summary>OAuth 2.0 token response (RFC 6749 §5.1).</summary>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>OAuth 2.0 error response (RFC 6749 §5.2).</summary>
public sealed record TokenError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string Description);

/// <summary>
/// Issues access and refresh tokens as ASP.NET Core bearer tokens (Data Protection, validated by the BearerToken
/// handler). OpenIddict is not AOT-compatible (ADR-0039); first-party clients keep the OAuth password and
/// refresh-token grants on <c>/connect/token</c>. A token carries no scopes unless it was requested with some: its
/// permissions are the user's effective scopes, checked on every request.
/// </summary>
internal sealed class TokenIssuer(IOptionsMonitor<BearerTokenOptions> bearer, TimeProvider time)
{
    private const string RefreshPurpose = BearerTokenDefaults.AuthenticationScheme + ":RefreshToken";

    /// <summary>Scope values of OAuth clients that do not limit a token (the whole API, refresh tokens, OpenID).</summary>
    private static readonly HashSet<string> Unlimiting = new(["api", "offline_access", "openid", "profile", "email"], StringComparer.Ordinal);

    /// <summary>
    /// The scopes a <c>scope</c> parameter limits a token to: null for none (the user's scopes), or an error for
    /// unknown scopes.
    /// </summary>
    public static (IReadOnlyList<string>? Limit, string? Error) ParseScope(string? value, IScopeCatalog catalog)
    {
        var names = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(n => !Unlimiting.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return (null, null);
        }

        return names.Where(n => !catalog.Contains(n)).ToList() is { Count: > 0 } unknown
            ? (null, $"Unknown scopes: {string.Join(", ", unknown)}.")
            : (names, null);
    }

    public TokenResponse Issue(User user, IReadOnlyCollection<string>? limit, IReadOnlySet<string> effective)
    {
        var options = bearer.Get(BearerTokenDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(BearerTokenDefaults.AuthenticationScheme, PaperDotNetClaims.UserName, null);
        identity.AddClaim(new Claim(PaperDotNetClaims.UserId, user.Id.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.TenantId, user.TenantId.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.UserName, user.UserName));
        identity.AddClaim(new Claim(PaperDotNetClaims.SecurityStamp, user.SecurityStamp));
        if (limit is not null)
        {
            identity.AddClaim(new Claim(PaperDotNetClaims.Scope, ScopeList.Format(limit)));
        }

        var principal = new ClaimsPrincipal(identity);
        var now = time.GetUtcNow();
        var access = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = now + options.BearerTokenExpiration }, BearerTokenDefaults.AuthenticationScheme);
        var refresh = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = now + options.RefreshTokenExpiration }, RefreshPurpose);
        return new TokenResponse(
            options.BearerTokenProtector.Protect(access),
            "Bearer",
            (long)options.BearerTokenExpiration.TotalSeconds,
            options.RefreshTokenProtector.Protect(refresh),
            ScopeList.Format(limit is null ? effective : limit.Where(effective.Contains)));
    }

    /// <summary>The user and tenant of a valid, unexpired refresh token, with its security stamp and scope limit.</summary>
    public (Guid UserId, Guid TenantId, string Stamp, IReadOnlyList<string>? Limit)? ReadRefreshToken(string token)
    {
        var options = bearer.Get(BearerTokenDefaults.AuthenticationScheme);
        var ticket = options.RefreshTokenProtector.Unprotect(token);
        if (ticket?.Properties.ExpiresUtc is not { } expires || time.GetUtcNow() >= expires)
        {
            return null;
        }

        var limit = ticket.Principal.FindFirst(PaperDotNetClaims.Scope)?.Value is { } scopes ? ScopeList.Parse(scopes) : null;
        return ticket.Principal.FindGuid(PaperDotNetClaims.UserId) is { } userId && ticket.Principal.FindGuid(PaperDotNetClaims.TenantId) is { } tenantId
            ? (userId, tenantId, ticket.Principal.FindFirst(PaperDotNetClaims.SecurityStamp)?.Value ?? "", limit)
            : null;
    }
}

internal static class TokenEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/connect/token", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimits.SignIn)
            .WithTags("Auth")
            .WithSummary("OAuth 2.0 token endpoint: password and refresh_token grants (form encoded).");

    private static async Task<Results<Ok<TokenResponse>, BadRequest<TokenError>>> HandleAsync(
        HttpRequest request, IdentityDbContext db, TokenIssuer issuer, IPasswordHasher<User> hasher, IEffectiveScopeProvider scopes,
        IScopeCatalog catalog, IOptions<TenancyOptions> tenancy, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Invalid("invalid_request", "The request must be form encoded.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        switch (form["grant_type"].ToString())
        {
            case "password":
                {
                    var (limit, scopeError) = TokenIssuer.ParseScope(form["scope"].ToString(), catalog);
                    if (scopeError is not null)
                    {
                        return Invalid("invalid_scope", scopeError);
                    }

                    var identifier = form["tenant"].ToString() is { Length: > 0 } requested ? requested : tenancy.Value.DefaultTenant;
                    var user = await Users.FindForSignInAsync(db, identifier, form["username"].ToString(), cancellationToken);
                    if (user is null || !await SignInAsync(db, hasher, user, form["password"].ToString(), time.GetUtcNow(), cancellationToken))
                    {
                        return Invalid("invalid_grant", "The user name or password is not correct.");
                    }

                    var effective = await scopes.GetScopesAsync(user.TenantId, user.Id, cancellationToken) ?? new HashSet<string>();
                    return TypedResults.Ok(issuer.Issue(user, limit, effective));
                }

            case "refresh_token":
                {
                    if (issuer.ReadRefreshToken(form["refresh_token"].ToString()) is not { } refresh
                        || await Users.FindAsync(db, refresh.TenantId, refresh.UserId, cancellationToken) is not { IsDisabled: false } user
                        || user.SecurityStamp != refresh.Stamp
                        || !await TenantDirectory.IsActiveAsync(db, user.TenantId, cancellationToken))
                    {
                        return Invalid("invalid_grant", "The refresh token is not valid.");
                    }

                    var effective = await scopes.GetScopesAsync(user.TenantId, user.Id, cancellationToken) ?? new HashSet<string>();
                    return TypedResults.Ok(issuer.Issue(user, refresh.Limit, effective));
                }

            default:
                return Invalid("unsupported_grant_type", "Use the password or refresh_token grant.");
        }
    }

    /// <summary>
    /// Checks the password of an enabled user. After <see cref="Users.MaxFailedSignIns"/> failures in a row the
    /// account is locked for <see cref="Users.LockoutDuration"/>, and even the right password fails until then.
    /// </summary>
    private static async Task<bool> SignInAsync(IdentityDbContext db, IPasswordHasher<User> hasher, User user, string password, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (user.IsDisabled || user.LockoutEnd > now)
        {
            return false;
        }

        var result = user.PasswordHash.Length == 0 ? PasswordVerificationResult.Failed : hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= Users.MaxFailedSignIns)
            {
                user.AccessFailedCount = 0;
                user.LockoutEnd = now + Users.LockoutDuration;
            }

            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded || user.AccessFailedCount > 0 || user.LockoutEnd is not null)
        {
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, password);
            }

            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private static BadRequest<TokenError> Invalid(string error, string description) => TypedResults.BadRequest(new TokenError(error, description));
}

public sealed class TenancyOptions
{
    /// <summary>Tenant used when a token request names none.</summary>
    public string DefaultTenant { get; set; } = "default";
}

internal static class RateLimits
{
    public const string SignIn = "sign-in";
}
