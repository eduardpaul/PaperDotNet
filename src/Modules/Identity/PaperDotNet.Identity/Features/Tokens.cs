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
/// refresh-token grants on <c>/connect/token</c>.
/// </summary>
internal sealed class TokenIssuer(IOptionsMonitor<BearerTokenOptions> bearer, TimeProvider time)
{
    private const string RefreshPurpose = BearerTokenDefaults.AuthenticationScheme + ":RefreshToken";

    public TokenResponse Issue(User user)
    {
        var options = bearer.Get(BearerTokenDefaults.AuthenticationScheme);
        var scope = Scopes.For(user.IsAdmin);
        var identity = new ClaimsIdentity(BearerTokenDefaults.AuthenticationScheme, PaperDotNetClaims.UserName, null);
        identity.AddClaim(new Claim(PaperDotNetClaims.UserId, user.Id.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.TenantId, user.TenantId.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.UserName, user.UserName));
        identity.AddClaim(new Claim(PaperDotNetClaims.Scope, scope));
        identity.AddClaim(new Claim(PaperDotNetClaims.SecurityStamp, user.SecurityStamp));
        var principal = new ClaimsPrincipal(identity);

        var now = time.GetUtcNow();
        var access = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = now + options.BearerTokenExpiration }, BearerTokenDefaults.AuthenticationScheme);
        var refresh = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = now + options.RefreshTokenExpiration }, RefreshPurpose);
        return new TokenResponse(
            options.BearerTokenProtector.Protect(access),
            "Bearer",
            (long)options.BearerTokenExpiration.TotalSeconds,
            options.RefreshTokenProtector.Protect(refresh),
            scope);
    }

    /// <summary>The user and tenant of a valid, unexpired refresh token, with the security stamp it was issued for.</summary>
    public (Guid UserId, Guid TenantId, string Stamp)? ReadRefreshToken(string token)
    {
        var options = bearer.Get(BearerTokenDefaults.AuthenticationScheme);
        var ticket = options.RefreshTokenProtector.Unprotect(token);
        if (ticket?.Properties.ExpiresUtc is not { } expires || time.GetUtcNow() >= expires)
        {
            return null;
        }

        return ticket.Principal.FindGuid(PaperDotNetClaims.UserId) is { } userId && ticket.Principal.FindGuid(PaperDotNetClaims.TenantId) is { } tenantId
            ? (userId, tenantId, ticket.Principal.FindFirst(PaperDotNetClaims.SecurityStamp)?.Value ?? "")
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
        HttpRequest request, IdentityDbContext db, TokenIssuer issuer, IPasswordHasher<User> hasher, IOptions<TenancyOptions> tenancy, CancellationToken cancellationToken)
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
                    var identifier = form["tenant"].ToString() is { Length: > 0 } requested ? requested : tenancy.Value.DefaultTenant;
                    var user = await Users.FindForSignInAsync(db, identifier, form["username"].ToString(), cancellationToken);
                    if (user is null || user.IsDisabled
                        || hasher.VerifyHashedPassword(user, user.PasswordHash, form["password"].ToString()) == PasswordVerificationResult.Failed)
                    {
                        return Invalid("invalid_grant", "The user name or password is not correct.");
                    }

                    return TypedResults.Ok(issuer.Issue(user));
                }

            case "refresh_token":
                {
                    if (issuer.ReadRefreshToken(form["refresh_token"].ToString()) is not { } refresh
                        || await Users.FindAsync(db, refresh.TenantId, refresh.UserId, cancellationToken) is not { IsDisabled: false } user
                        || user.SecurityStamp != refresh.Stamp)
                    {
                        return Invalid("invalid_grant", "The refresh token is not valid.");
                    }

                    return TypedResults.Ok(issuer.Issue(user));
                }

            default:
                return Invalid("unsupported_grant_type", "Use the password or refresh_token grant.");
        }
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
