using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
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
        endpoints.MapGet(ReverseProxySignIn.SignInPath, ProxySignInAsync).AllowAnonymous().ExcludeFromDescription();
    }

    /// <summary>
    /// Sign-in through the reverse proxy (ADR-0045): the proxy authenticates only this path, outside <c>/connect/</c>,
    /// so basic-auth credentials never reach the OAuth endpoints. Starts the sign-in session and continues the
    /// authorization request in <paramref name="returnUrl"/> (only this server's <c>/connect/authorize</c>).
    /// </summary>
    private static async Task<IResult> ProxySignInAsync(
        HttpContext http, ITenantContext tenant, ReverseProxySignIn proxy, IOptions<AuthOptions> options, TimeProvider time, string? returnUrl)
    {
        if (tenant.TenantId is null)
        {
            return ApiErrors.Problem(StatusCodes.Status404NotFound, "tenantNotFound", "No active tenant matches this request.");
        }

        var authorize = $"{http.Request.PathBase}/connect/authorize";
        var target = returnUrl is not null
                     && (returnUrl.Equals(authorize, StringComparison.OrdinalIgnoreCase) || returnUrl.StartsWith(authorize + "?", StringComparison.OrdinalIgnoreCase))
            ? returnUrl
            : null;
        if (await proxy.AuthenticateAsync(http, http.RequestAborted) is { } user)
        {
            await AuthEndpoints.SignInSessionAsync(http, user, tenant, ReverseProxySignIn.Method, time.GetUtcNow());
            // This request has completed the requested sign-in. Continuing with prompt=login would demand
            // another sign-in forever; retain any other prompt values and every other OAuth parameter.
            if (target is not null && target.IndexOf('?') is var queryStart && queryStart >= 0)
            {
                var query = QueryHelpers.ParseQuery(target[queryStart..]);
                if (query.TryGetValue(Parameters.Prompt, out var prompt))
                {
                    var remaining = prompt.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(value => value != PromptValues.Login).ToArray();
                    if (remaining.Length == 0)
                    {
                        query.Remove(Parameters.Prompt);
                    }
                    else
                    {
                        query[Parameters.Prompt] = string.Join(' ', remaining);
                    }

                    target = target[..queryStart] + QueryString.Create(query);
                }
            }

            return Results.Redirect(target ?? $"{http.Request.PathBase}/");
        }

        // The proxy named nobody (or is not trusted): the password sign-in, when there is one, is the way in.
        if (options.Value is { LocalSignIn: true, LoginUrl: { Length: > 0 } loginUrl })
        {
            var separator = loginUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            return Results.Redirect(target is null ? loginUrl : $"{loginUrl}{separator}returnUrl={Uri.EscapeDataString(target)}");
        }

        return ApiErrors.Problem(StatusCodes.Status401Unauthorized, "proxySignInFailed", "The reverse proxy did not sign anyone in.");
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http, ITenantContext tenant, UserManager<User> users, IOptions<AuthOptions> options, ReverseProxySignIn proxy, TimeProvider time)
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
        // An authenticating reverse proxy (IAM-15) vouches for the user; its identity replaces the session's.
        var now = time.GetUtcNow();
        if (await proxy.AuthenticateAsync(http, http.RequestAborted) is { } proxied)
        {
            await AuthEndpoints.SignInSessionAsync(http, proxied, tenant, ReverseProxySignIn.Method, now);
            return SignIn(ProxyPrincipal(Principal(proxied, tenant, request.GetScopes()), now, options.Value, now)!);
        }

        // A session the proxy started lasts only as long as a proxy sign-in (ADR-0045); then the proxy is asked again.
        // Without local sign-in, only sessions the proxy started count (sessions from before the switch end here).
        var proxySignedInAt = ReverseProxySignIn.SignedInAt(session.Principal);
        if (user is null || !CanSignIn(user) || request.HasPromptValue(PromptValues.Login)
            || (!options.Value.LocalSignIn && proxySignedInAt is null)
            || session.Principal!.FindFirstValue(AuthEndpoints.SessionStampClaim) != user.SecurityStamp
            || (proxySignedInAt is { } at && ProxyTimeLeft(at, options.Value, now) <= TimeSpan.Zero))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Error(Errors.LoginRequired, "The user is not signed in.");
            }

            var returnUrl = $"{http.Request.PathBase}{http.Request.Path}{http.Request.QueryString}";
            if (options.Value.ReverseProxy.Enabled)
            {
                return Results.Redirect($"{http.Request.PathBase}{ReverseProxySignIn.SignInPath}?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            if (options.Value.LoginUrl is { Length: > 0 } loginUrl)
            {
                var separator = loginUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                return Results.Redirect($"{loginUrl}{separator}returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            return Results.Challenge(authenticationSchemes: [AuthSchemes.Session]);
        }

        var principal = Principal(user, tenant, request.GetScopes());
        return SignIn(proxySignedInAt is { } signedInAt ? ProxyPrincipal(principal, signedInAt, options.Value, now)! : principal);
    }

    private static TimeSpan ProxyTimeLeft(DateTimeOffset signedInAt, AuthOptions options, DateTimeOffset now) =>
        signedInAt + options.ReverseProxy.RefreshTokenLifetime - now;

    /// <summary>
    /// Marks the grant as a proxy sign-in and limits its tokens to what is left of it, so refreshing never outlives
    /// <see cref="ReverseProxyAuthOptions.RefreshTokenLifetime"/>; null when nothing is left.
    /// </summary>
    private static ClaimsPrincipal? ProxyPrincipal(ClaimsPrincipal principal, DateTimeOffset signedInAt, AuthOptions options, DateTimeOffset now)
    {
        var left = ProxyTimeLeft(signedInAt, options, now);
        if (left <= TimeSpan.Zero)
        {
            return null;
        }

        // Without destinations: kept in authorization codes and refresh tokens, never in access or identity tokens.
        ((ClaimsIdentity)principal.Identity!).AddClaim(ReverseProxySignIn.SignedInAtClaimFor(signedInAt));
        principal.SetRefreshTokenLifetime(left);
        if (left < options.AccessTokenLifetime)
        {
            principal.SetAccessTokenLifetime(left);
        }

        return principal;
    }

    private static async Task<IResult> TokenAsync(
        HttpContext http, ITenantContext tenant, UserManager<User> users, IOpenIddictApplicationManager applications, IOptions<AuthOptions> options,
        TimeProvider time)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OpenID Connect request.");
        if (tenant.TenantId is not { } tenantId)
        {
            return Error(Errors.InvalidRequest, "No active tenant matches this request.");
        }

        if (request.IsPasswordGrantType())
        {
            if (!options.Value.LocalSignIn)
            {
                return Error(Errors.UnauthorizedClient, "Sign-in with a password is turned off here; use an API token or sign in through the proxy.");
            }

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
            if (user is null || user.IsDisabled)
            {
                return Error(Errors.InvalidGrant, "The grant is no longer valid.");
            }

            var principal = Principal(user, tenant, granted!.GetScopes());
            if (ReverseProxySignIn.SignedInAt(granted) is not { } signedInAt)
            {
                return SignIn(principal);
            }

            // A proxy sign-in ends at the same time however often it is refreshed (ADR-0045).
            return ProxyPrincipal(principal, signedInAt, options.Value, time.GetUtcNow()) is { } proxied
                ? SignIn(proxied)
                : Error(Errors.InvalidGrant, "The sign-in through the proxy has ended; sign in again.");
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

    /// <summary>
    /// Ends the sign-in session and redirects to the client's registered post-logout URI; after a proxy sign-in, to the
    /// proxy's logout page when one is configured, since the proxy would otherwise sign the user straight back in.
    /// </summary>
    private static async Task<IResult> LogoutAsync(HttpContext http, IOptions<AuthOptions> options)
    {
        var session = await http.AuthenticateAsync(AuthSchemes.Session);
        await http.SignOutAsync(AuthSchemes.Session);
        if (options.Value.ReverseProxy is { Enabled: true, LogoutUrl: { Length: > 0 } logoutUrl }
            && session.Principal?.FindFirstValue("amr") == ReverseProxySignIn.Method)
        {
            return Results.Redirect(logoutUrl);
        }

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
