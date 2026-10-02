using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

/// <summary>Configuration section <c>Auth</c>.</summary>
public sealed class AuthOptions
{
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>How long a sign-in session (cookie, used by <c>/connect/authorize</c>) lasts without use.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>The password grant of the first-party client <c>paperdotnet</c> (CLI, scripts); other clients never get it.</summary>
    public bool AllowPasswordGrant { get; set; } = true;

    /// <summary>
    /// The web UI's sign-in page: <c>/connect/authorize</c> redirects there with <c>returnUrl</c> when there is no
    /// session. Without it, 401.
    /// </summary>
    public string? LoginUrl { get; set; }

    /// <summary>Redirect URIs of the first-party client (e.g. the web UI's callback): turns on its authorization code grant.</summary>
    public List<string> FirstPartyRedirectUris { get; set; } = [];

    /// <summary>The relying party id of passkeys (the web UI's domain); the request's host when not set.</summary>
    public string? PasskeyServerDomain { get; set; }

    /// <summary>Origins that may use passkeys (e.g. <c>https://docs.example.com</c>); the request's origin when empty.</summary>
    public List<string> PasskeyOrigins { get; set; } = [];

    /// <summary>Sign-in through an authenticating reverse proxy (IAM-15), off by default.</summary>
    public Authentication.ReverseProxyAuthOptions ReverseProxy { get; set; } = new();
}

/// <summary>OAuth 2.0 token response (RFC 6749 §5.1), with an OpenID Connect identity token when <c>openid</c> was granted.</summary>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("refresh_token"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RefreshToken,
    [property: JsonPropertyName("scope")] string Scope)
{
    [JsonPropertyName("id_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdToken { get; init; }
}

/// <summary>OAuth 2.0 error response (RFC 6749 §5.2).</summary>
public sealed record TokenError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string Description);

/// <summary>
/// Issues access and refresh tokens as ASP.NET Core bearer tokens (Data Protection, validated by the BearerToken
/// handler). OpenIddict is not AOT-compatible (ADR-0039). A token is limited to the permission scopes it was granted
/// unless it has none or the <c>api</c> scope (the first-party client): then its permissions are the user's effective
/// scopes, checked on every request. Tokens name the client they were issued to.
/// </summary>
internal sealed class TokenIssuer(IOptionsMonitor<BearerTokenOptions> bearer, TimeProvider time)
{
    public const string ClientClaim = "cid";

    private const string RefreshPurpose = BearerTokenDefaults.AuthenticationScheme + ":RefreshToken";

    /// <summary>Scope values of OAuth clients that do not limit a token (the whole API, refresh tokens, OpenID).</summary>
    private static readonly HashSet<string> Unlimiting = new([OAuth.Api, OAuth.OfflineAccess, OAuth.OpenId, OAuth.Profile, "email"], StringComparer.Ordinal);

    /// <summary>
    /// The scopes a <c>scope</c> parameter of the first-party client limits a token to: null for none (the user's
    /// scopes), or an error for unknown scopes.
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

    /// <summary>What granted OAuth scopes allow: everything with <c>api</c>, else only the permission scopes among them (possibly none).</summary>
    public static IReadOnlyList<string>? LimitOf(IReadOnlyList<string> granted) =>
        granted.Contains(OAuth.Api) ? null : [.. granted.Where(s => !Unlimiting.Contains(s))];

    public TimeSpan AccessTokenLifetime => bearer.Get(BearerTokenDefaults.AuthenticationScheme).BearerTokenExpiration;

    public TokenResponse Issue(User user, IReadOnlyCollection<string>? limit, IReadOnlySet<string> effective, string clientId = OAuthClient.FirstPartyClientId, bool refresh = true)
    {
        var options = bearer.Get(BearerTokenDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(BearerTokenDefaults.AuthenticationScheme, PaperDotNetClaims.UserName, null);
        identity.AddClaim(new Claim(PaperDotNetClaims.UserId, user.Id.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.TenantId, user.TenantId.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.UserName, user.UserName));
        identity.AddClaim(new Claim(PaperDotNetClaims.SecurityStamp, user.SecurityStamp));
        identity.AddClaim(new Claim(ClientClaim, clientId));
        if (limit is not null)
        {
            identity.AddClaim(new Claim(PaperDotNetClaims.Scope, ScopeList.Format(limit)));
        }

        var principal = new ClaimsPrincipal(identity);
        var now = time.GetUtcNow();
        var access = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = now + options.BearerTokenExpiration }, BearerTokenDefaults.AuthenticationScheme);
        var refreshTicket = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = now + options.RefreshTokenExpiration }, RefreshPurpose);
        return new TokenResponse(
            options.BearerTokenProtector.Protect(access),
            "Bearer",
            (long)options.BearerTokenExpiration.TotalSeconds,
            refresh ? options.RefreshTokenProtector.Protect(refreshTicket) : null,
            ScopeList.Format(limit is null ? effective : limit.Where(effective.Contains)));
    }

    /// <summary>The user, tenant and client of a valid, unexpired refresh token, with its security stamp and scope limit.</summary>
    public (Guid UserId, Guid TenantId, string ClientId, string Stamp, IReadOnlyList<string>? Limit)? ReadRefreshToken(string token)
    {
        var options = bearer.Get(BearerTokenDefaults.AuthenticationScheme);
        var ticket = options.RefreshTokenProtector.Unprotect(token);
        if (ticket?.Properties.ExpiresUtc is not { } expires || time.GetUtcNow() >= expires)
        {
            return null;
        }

        var principal = ticket.Principal;
        var limit = principal.FindFirst(PaperDotNetClaims.Scope)?.Value is { } scopes ? ScopeList.Parse(scopes) : null;
        return principal.FindGuid(PaperDotNetClaims.UserId) is { } userId && principal.FindGuid(PaperDotNetClaims.TenantId) is { } tenantId
            ? (userId, tenantId, principal.FindFirst(ClientClaim)?.Value ?? OAuthClient.FirstPartyClientId, principal.FindFirst(PaperDotNetClaims.SecurityStamp)?.Value ?? "", limit)
            : null;
    }
}

/// <summary>
/// The OAuth 2.0 token endpoint (IAM-02): <c>authorization_code</c> (PKCE), <c>refresh_token</c>,
/// <c>client_credentials</c> (as the client's service account) and <c>password</c> (first-party client only).
/// Confidential clients authenticate with HTTP Basic or <c>client_secret</c>; without <c>client_id</c> the client is
/// the first-party one.
/// </summary>
internal static class TokenEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/connect/token", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimits.SignIn)
            .WithTags("Auth")
            .WithSummary("OAuth 2.0 token endpoint: authorization_code, refresh_token, client_credentials and password grants (form encoded).");

    private static async Task<Results<Ok<TokenResponse>, BadRequest<TokenError>, UnauthorizedHttpResult>> HandleAsync(
        HttpRequest request, IdentityDbContext db, TokenIssuer issuer, ServerKeys keys, IPasswordHasher<User> hasher, IEffectiveScopeProvider scopes,
        IScopeCatalog catalog, IOptions<TenancyOptions> tenancy, IOptions<AuthOptions> auth, TenantResolver tenants, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Invalid("invalid_request", "The request must be form encoded.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var (clientId, secret) = ClientCredentials(request, form);
        var grant = form["grant_type"].ToString();
        if (grant == OAuth.Password)
        {
            if (clientId is not (null or OAuthClient.FirstPartyClientId) || !auth.Value.AllowPasswordGrant)
            {
                return Invalid("unauthorized_client", "The password grant is only available to the first-party client.");
            }

            var (limit, scopeError) = TokenIssuer.ParseScope(form["scope"].ToString(), catalog);
            if (scopeError is not null)
            {
                return Invalid("invalid_scope", scopeError);
            }

            // The tenant: the request's parameter, else the one its host or header names, else the default.
            var named = await tenants.ResolveAsync(request, cancellationToken);
            var identifier = form["tenant"].ToString() is { Length: > 0 } requested ? requested : named?.Identifier ?? tenancy.Value.DefaultTenant;
            if (named is not null && !string.Equals(named.Identifier, identifier, StringComparison.Ordinal))
            {
                return Invalid("invalid_request", "The tenant parameter names another tenant than the host or header.");
            }

            var user = await Users.FindForSignInAsync(db, identifier, form["username"].ToString(), cancellationToken);
            if (user is null || !SignInSession.CanSignIn(user) || !await CheckPasswordAsync(db, hasher, user, form["password"].ToString(), time.GetUtcNow(), cancellationToken))
            {
                return Invalid("invalid_grant", "The user name or password is not correct.");
            }

            var effective = await scopes.GetScopesAsync(user.TenantId, user.Id, cancellationToken) ?? new HashSet<string>();
            return TypedResults.Ok(issuer.Issue(user, limit, effective));
        }

        if (grant == OAuth.RefreshToken)
        {
            if (issuer.ReadRefreshToken(form["refresh_token"].ToString()) is not { } refresh
                || (clientId is not null && clientId != refresh.ClientId)
                || await Users.FindAsync(db, refresh.TenantId, refresh.UserId, cancellationToken) is not { IsDisabled: false } user
                || user.SecurityStamp != refresh.Stamp
                || !await TenantDirectory.IsActiveAsync(db, user.TenantId, cancellationToken))
            {
                return Invalid("invalid_grant", "The refresh token is not valid.");
            }

            // The client must still exist (and, if confidential, authenticate).
            if (await Applications.FindAsync(db, user.TenantId, refresh.ClientId, auth.Value, cancellationToken) is not { } client || !Applications.Authenticates(client, secret))
            {
                return Invalid("invalid_grant", "The refresh token is not valid.");
            }

            var effective = await scopes.GetScopesAsync(user.TenantId, user.Id, cancellationToken) ?? new HashSet<string>();
            return TypedResults.Ok(issuer.Issue(user, refresh.Limit, effective, client.ClientId));
        }

        if (grant is not (OAuth.AuthorizationCode or OAuth.ClientCredentials))
        {
            return Invalid("unsupported_grant_type", "Use authorization_code, refresh_token, client_credentials or password.");
        }

        // Grants of registered clients: the client must authenticate in the request's tenant.
        var tenant = await tenants.ResolveOrDefaultAsync(request, cancellationToken);
        if (tenant.Id is not { } tenantId || clientId is null
            || await Applications.FindAsync(db, tenantId, clientId, auth.Value, cancellationToken) is not { } registered
            || !Applications.Authenticates(registered, secret))
        {
            return Invalid("invalid_client", "The client is unknown or did not authenticate.");
        }

        if (!OAuth.Split(registered.GrantTypes).Contains(grant))
        {
            return Invalid("unauthorized_client", $"The client may not use the {grant} grant.");
        }

        return grant == OAuth.AuthorizationCode
            ? await RedeemCodeAsync(request, form, registered, tenant.Identifier, db, issuer, keys, scopes, time, cancellationToken)
            : await ClientCredentialsAsync(form, registered, db, issuer, scopes, cancellationToken);
    }

    private static async Task<Results<Ok<TokenResponse>, BadRequest<TokenError>, UnauthorizedHttpResult>> RedeemCodeAsync(
        HttpRequest request, IFormCollection form, OAuthClient client, string tenantIdentifier, IdentityDbContext db, TokenIssuer issuer, ServerKeys keys,
        IEffectiveScopeProvider scopes, TimeProvider time, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = client.TenantId;
        var hash = OAuth.HashSecret(form["code"].ToString());
        var ct = cancellationToken;
        var code = await database.OAuthCodes.Where(c => c.TenantId == tenant && c.CodeHash == hash).FirstOrDefaultAsync(ct);
        if (code is null)
        {
            return Invalid("invalid_grant", "The code is not valid.");
        }

        // Single use: whoever deletes the row redeems it.
        database.OAuthCodes.Remove(code);
        try
        {
            await database.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Invalid("invalid_grant", "The code is not valid.");
        }

        var now = time.GetUtcNow();
        if (code.ExpiresAt <= now.ToUnixTimeMilliseconds() || code.ClientId != client.ClientId || code.RedirectUri != form["redirect_uri"].ToString()
            || !OpenIdConnect.VerifyPkce(form["code_verifier"].ToString(), code.CodeChallenge)
            || await Users.FindAsync(database, tenant, code.UserId, ct) is not { } user
            || !SignInSession.CanSignIn(user) || user.SecurityStamp != code.SecurityStamp)
        {
            return Invalid("invalid_grant", "The code is not valid.");
        }

        var granted = OAuth.Split(code.Scope);
        var effective = await scopes.GetScopesAsync(user.TenantId, user.Id, ct) ?? new HashSet<string>();
        var refresh = granted.Contains(OAuth.OfflineAccess) && OAuth.Split(client.GrantTypes).Contains(OAuth.RefreshToken);
        var response = issuer.Issue(user, TokenIssuer.LimitOf(granted), effective, client.ClientId, refresh);
        return TypedResults.Ok(granted.Contains(OAuth.OpenId)
            ? response with { IdToken = await OpenIdConnect.IdTokenAsync(keys, request, user, tenantIdentifier, client.ClientId, code.Nonce, issuer.AccessTokenLifetime, now, ct) }
            : response);
    }

    private static async Task<Results<Ok<TokenResponse>, BadRequest<TokenError>, UnauthorizedHttpResult>> ClientCredentialsAsync(
        IFormCollection form, OAuthClient client, IdentityDbContext db, TokenIssuer issuer, IEffectiveScopeProvider scopes, CancellationToken cancellationToken)
    {
        var allowed = OAuth.Split(client.Scopes);
        var requested = OAuth.Split(form["scope"].ToString());
        if (requested.FirstOrDefault(s => !allowed.Contains(s)) is { } notAllowed)
        {
            return Invalid("invalid_scope", $"The client may not request the scope '{notAllowed}'.");
        }

        if (client.ServiceUserId is not { } serviceUserId || await Users.FindAsync(db, client.TenantId, serviceUserId, cancellationToken) is not { IsDisabled: false } account)
        {
            return Invalid("invalid_client", "The client has no active service account.");
        }

        var effective = await scopes.GetScopesAsync(account.TenantId, account.Id, cancellationToken) ?? new HashSet<string>();
        return TypedResults.Ok(issuer.Issue(account, TokenIssuer.LimitOf(requested.Count > 0 ? requested : allowed), effective, client.ClientId, refresh: false));
    }

    /// <summary>The client id and secret from HTTP Basic authentication or the form.</summary>
    private static (string? ClientId, string? Secret) ClientCredentials(HttpRequest request, IFormCollection form)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var colon = decoded.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    return (Uri.UnescapeDataString(decoded[..colon]), Uri.UnescapeDataString(decoded[(colon + 1)..]));
                }
            }
            catch (FormatException)
            {
                return (null, null);
            }
        }

        return (form["client_id"].ToString() is { Length: > 0 } id ? id : null, form["client_secret"].ToString() is { Length: > 0 } secret ? secret : null);
    }

    /// <summary>
    /// Checks the password of an enabled user. After <see cref="Users.MaxFailedSignIns"/> failures in a row the
    /// account is locked for <see cref="Users.LockoutDuration"/>, and even the right password fails until then.
    /// </summary>
    internal static async Task<bool> CheckPasswordAsync(IdentityDbContext db, IPasswordHasher<User> hasher, User user, string password, DateTimeOffset now, CancellationToken cancellationToken)
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

internal static class RateLimits
{
    public const string SignIn = "sign-in";
}
