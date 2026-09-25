using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace PaperDotNet.Identity.Features;

/// <summary>
/// OAuth 2.0 / OpenID Connect endpoints (IAM-02, OpenIddict passthrough): authorization
/// code + PKCE with the sign-in session, refresh tokens, client credentials (as the
/// client's service account) and the password grant for the first-party client.
/// Registered clients are trusted by the organization, so there is no consent screen.
/// </summary>
internal static class OAuthEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var connect = endpoints.MapGroup("/connect").AllowAnonymous().ExcludeFromDescription();
        connect.MapMethods("/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync);
        connect.MapPost("/token", TokenAsync);
        connect.MapMethods("/userinfo", [HttpMethods.Get, HttpMethods.Post], UserInfoAsync);
        connect.MapMethods("/logout", [HttpMethods.Get, HttpMethods.Post], (Delegate)LogoutAsync);
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http, ITenantContext tenant, UserManager<User> users, IOptions<AuthOptions> options)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OpenID Connect request.");
        if (tenant.TenantId is not { } tenantId)
        {
            return Error(Errors.InvalidRequest, "No active tenant matches this request.");
        }

        var session = await http.AuthenticateAsync(AuthSchemes.Session);
        var user = session.Succeeded && session.Principal.FindFirstValue(PaperDotNetClaims.TenantId) == tenantId.ToString()
            ? await users.FindByIdAsync(session.Principal.FindFirstValue(PaperDotNetClaims.UserId)!)
            : null;
        if (user is null || !CanSignIn(user) || request.HasPromptValue(PromptValues.Login)
            || session.Principal!.FindFirstValue(AuthEndpoints.SessionStampClaim) != user.SecurityStamp)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Error(Errors.LoginRequired, "The user is not signed in.");
            }

            if (options.Value.LoginUrl is { Length: > 0 } loginUrl)
            {
                var returnUrl = $"{http.Request.PathBase}{http.Request.Path}{http.Request.QueryString}";
                var separator = loginUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                return Results.Redirect($"{loginUrl}{separator}returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            return Results.Challenge(authenticationSchemes: [AuthSchemes.Session]);
        }

        return SignIn(Principal(user, tenant, request.GetScopes()));
    }

    private static async Task<IResult> TokenAsync(
        HttpContext http, ITenantContext tenant, UserManager<User> users, IOpenIddictApplicationManager applications, IOptions<AuthOptions> options)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OpenID Connect request.");
        if (tenant.TenantId is not { } tenantId)
        {
            return Error(Errors.InvalidRequest, "No active tenant matches this request.");
        }

        if (request.IsPasswordGrantType())
        {
            if (!options.Value.AllowPasswordGrant || request.ClientId != OAuthApplication.FirstPartyClientId)
            {
                return Error(Errors.UnauthorizedClient, "The password grant is only available to the first-party client.");
            }

            var user = await PasswordSignIn.CheckAsync(users, request.Username, request.Password);
            return user is null
                ? Error(Errors.InvalidGrant, "The user name or password is incorrect.")
                : SignIn(Principal(user, tenant, request.GetScopes()));
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var granted = result.Principal;
            var user = granted?.FindFirstValue(PaperDotNetClaims.TenantId) == tenantId.ToString()
                ? await users.FindByIdAsync(granted!.FindFirstValue(Claims.Subject)!)
                : null;

            // Claims are rebuilt, so name changes and disabled accounts take effect on refresh.
            return user is null || user.IsDisabled
                ? Error(Errors.InvalidGrant, "The grant is no longer valid.")
                : SignIn(Principal(user, tenant, granted!.GetScopes()));
        }

        if (request.IsClientCredentialsGrantType())
        {
            var application = await applications.FindByClientIdAsync(request.ClientId!) as OAuthApplication;
            var user = application?.ServiceUserId is { } serviceUserId ? await users.FindByIdAsync(serviceUserId.ToString()) : null;
            return user is null || user.IsDisabled
                ? Error(Errors.InvalidClient, "The client has no active service account.")
                : SignIn(Principal(user, tenant, request.GetScopes()));
        }

        return Error(Errors.UnsupportedGrantType, "The grant type is not supported.");
    }

    private static async Task<IResult> UserInfoAsync(HttpContext http, ITenantContext tenant)
    {
        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (result.Principal is not { } principal || principal.FindFirstValue(PaperDotNetClaims.TenantId) != tenant.TenantId?.ToString())
        {
            return Results.Challenge(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        return Results.Ok(new Dictionary<string, string?>
        {
            [Claims.Subject] = principal.FindFirstValue(Claims.Subject),
            [Claims.Name] = principal.FindFirstValue(PaperDotNetClaims.Name),
            [PaperDotNetClaims.TenantId] = principal.FindFirstValue(PaperDotNetClaims.TenantId),
            [PaperDotNetClaims.TenantIdentifier] = principal.FindFirstValue(PaperDotNetClaims.TenantIdentifier),
        });
    }

    /// <summary>Ends the sign-in session and redirects to the client's registered post-logout URI.</summary>
    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(AuthSchemes.Session);
        return Results.SignOut(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    internal static bool CanSignIn(User user) => !user.IsDisabled && !user.IsServiceAccount;

    /// <summary>
    /// The token principal: identity only (user, tenant). Authorization always uses the user's
    /// current roles; without the <c>api</c> scope the token is also limited to the permission
    /// scopes it was granted (like API tokens).
    /// </summary>
    internal static ClaimsPrincipal Principal(User user, ITenantContext tenant, ImmutableArray<string> scopes)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, PaperDotNetClaims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(PaperDotNetClaims.Name, user.DisplayName ?? user.UserName)
            .SetClaim(PaperDotNetClaims.TenantId, tenant.TenantId!.Value.ToString())
            .SetClaim(PaperDotNetClaims.TenantIdentifier, tenant.TenantIdentifier);
        identity.SetScopes(scopes);
        if (!scopes.Contains(OAuthScopes.Api))
        {
            identity.SetClaim(PaperDotNetClaims.ScopeLimited, "true");
            foreach (var scope in scopes.Where(s => s is not (Scopes.OpenId or Scopes.Profile or Scopes.OfflineAccess)))
            {
                identity.AddClaim(new Claim(PaperDotNetClaims.TokenScope, scope));
            }
        }

        identity.SetDestinations(claim => claim.Type is Claims.Subject or PaperDotNetClaims.Name or PaperDotNetClaims.TenantId or PaperDotNetClaims.TenantIdentifier
            ? [Destinations.AccessToken, Destinations.IdentityToken]
            : [Destinations.AccessToken]);
        return new ClaimsPrincipal(identity);
    }

    private static IResult SignIn(ClaimsPrincipal principal) =>
        Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    private static IResult Error(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}

/// <summary>Password check with lockout, shared by the sign-in session and the password grant.</summary>
internal static class PasswordSignIn
{
    public static async Task<User?> CheckAsync(UserManager<User> users, string? userName, string? password)
    {
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var user = await users.FindByNameAsync(userName);
        if (user is null || !OAuthEndpoints.CanSignIn(user) || await users.IsLockedOutAsync(user))
        {
            return null;
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            return null;
        }

        await users.ResetAccessFailedCountAsync(user);
        return user;
    }
}
