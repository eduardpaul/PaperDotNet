using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record LoginRequest(string? UserName, string? Password);

/// <summary>OpenID Connect discovery (the fields this server supports).</summary>
public sealed record DiscoveryDocument(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("userinfo_endpoint")] string UserInfoEndpoint,
    [property: JsonPropertyName("end_session_endpoint")] string EndSessionEndpoint,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("response_types_supported")] IReadOnlyList<string> ResponseTypes,
    [property: JsonPropertyName("grant_types_supported")] IReadOnlyList<string> GrantTypes,
    [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string> CodeChallengeMethods,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("subject_types_supported")] IReadOnlyList<string> SubjectTypes,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] IReadOnlyList<string> SigningAlgorithms,
    [property: JsonPropertyName("token_endpoint_auth_methods_supported")] IReadOnlyList<string> ClientAuthMethods);

public sealed record JsonWebKeyData(
    [property: JsonPropertyName("kty")] string KeyType,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("kid")] string KeyId,
    [property: JsonPropertyName("alg")] string Algorithm,
    [property: JsonPropertyName("n")] string Modulus,
    [property: JsonPropertyName("e")] string Exponent);

public sealed record JsonWebKeySetResponse([property: JsonPropertyName("keys")] IReadOnlyList<JsonWebKeyData> Keys);

public sealed record UserInfoResponse(
    [property: JsonPropertyName("sub")] string Subject,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("preferred_username")] string UserName,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("tid")] string TenantId,
    [property: JsonPropertyName("tenant")] string Tenant);

/// <summary>
/// The sign-in session (IAM-01): a cookie that only serves <c>/connect/authorize</c>; APIs are called with access
/// tokens or API tokens. It names the tenant and carries the user's security stamp, so a password change ends it.
/// </summary>
internal static class SignInSession
{
    public const string Scheme = "PaperDotNet.Session";
    private const string MethodClaim = "amr";

    public static Task SignInAsync(HttpContext http, User user, string method)
    {
        var identity = new ClaimsIdentity(Scheme, PaperDotNetClaims.UserName, null);
        identity.AddClaim(new Claim(PaperDotNetClaims.UserId, user.Id.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.TenantId, user.TenantId.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.UserName, user.UserName));
        identity.AddClaim(new Claim(PaperDotNetClaims.SecurityStamp, user.SecurityStamp));
        identity.AddClaim(new Claim(MethodClaim, method));
        return http.SignInAsync(Scheme, new ClaimsPrincipal(identity));
    }

    /// <summary>The signed-in user of the tenant, while the session is valid (same stamp, enabled, interactive).</summary>
    public static async Task<User?> UserAsync(HttpContext http, IdentityDbContext db, Guid tenantId)
    {
        var session = await http.AuthenticateAsync(Scheme);
        if (!session.Succeeded || session.Principal.FindGuid(PaperDotNetClaims.TenantId) != tenantId
            || session.Principal.FindGuid(PaperDotNetClaims.UserId) is not { } userId)
        {
            return null;
        }

        var user = await Users.FindAsync(db, tenantId, userId, http.RequestAborted);
        return user is not null && CanSignIn(user) && user.SecurityStamp == session.Principal.FindFirst(PaperDotNetClaims.SecurityStamp)?.Value ? user : null;
    }

    /// <summary>Interactive sign-in: enabled people, not the accounts of OAuth clients.</summary>
    public static bool CanSignIn(User user) => !user.IsDisabled && !user.IsServiceAccount && user.DeletedAt is null;
}

/// <summary>
/// The RSA key that signs identity tokens, shared by every server: created once and stored in the database with its
/// private key protected by Data Protection.
/// </summary>
internal sealed class ServerKeys(IServiceScopeFactory scopes, IDataProtectionProvider dataProtection, TimeProvider time) : IDisposable
{
    public const string Signing = "sig";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("PaperDotNet.Identity.ServerKeys");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RsaSecurityKey? _signing;

    public async Task<RsaSecurityKey> SigningKeyAsync(CancellationToken cancellationToken)
    {
        if (_signing is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _signing ??= await LoadOrCreateAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RsaSecurityKey> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var use = Signing;
        var ct = cancellationToken;
        var stored = await db.ServerKeys.AsNoTracking().Where(k => k.Use == use).OrderBy(k => k.Id).FirstOrDefaultAsync(ct);
        if (stored is null)
        {
            using var created = RSA.Create(2048);
            db.ServerKeys.Add(new ServerKey
            {
                Id = Ids.New(),
                Use = use,
                ProtectedKey = Convert.ToBase64String(_protector.Protect(created.ExportRSAPrivateKey())),
                CreatedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);

            // Two servers may create one at the same time: all of them use the oldest (ids are time-ordered).
            stored = await db.ServerKeys.AsNoTracking().Where(k => k.Use == use).OrderBy(k => k.Id).FirstOrDefaultAsync(ct);
        }

        var rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(_protector.Unprotect(Convert.FromBase64String(stored!.ProtectedKey)), out _);
        return new RsaSecurityKey(rsa) { KeyId = stored.Id.ToString("N") };
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// OpenID Connect on top of the OAuth endpoints (IAM-02): authorization code with PKCE (S256, required) through the
/// sign-in session, discovery, the signing key, user info and sign-out. OpenIddict is not AOT-compatible (ADR-0039);
/// identity tokens are JWTs signed with <see cref="JsonWebTokenHandler"/>.
/// </summary>
internal static class OpenIdConnect
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", Discovery).AllowAnonymous().WithTags("Auth").WithName("OpenIdConfiguration");
        app.MapGet("/.well-known/jwks", JwksAsync).AllowAnonymous().WithTags("Auth").WithName("JsonWebKeySet");
        app.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync).AllowAnonymous().DisableAntiforgery()
            .WithTags("Auth").WithName("Authorize")
            .WithSummary("OAuth 2.0 authorization endpoint: response_type=code with PKCE (S256), for the signed-in session's user.");
        app.MapMethods("/connect/userinfo", [HttpMethods.Get, HttpMethods.Post], UserInfoAsync).RequireAuthorization().DisableAntiforgery()
            .WithTags("Auth").WithName("UserInfo");
        app.MapMethods("/connect/logout", [HttpMethods.Get, HttpMethods.Post], LogoutAsync).AllowAnonymous().DisableAntiforgery()
            .WithTags("Auth").WithName("EndSession")
            .WithSummary("Ends the sign-in session; returns to post_logout_redirect_uri when the client registered it.");

        var auth = app.MapGroup("/v1.0/auth").WithTags("Auth").AllowAnonymous();
        auth.MapPost("/login", LoginAsync).RequireRateLimiting(RateLimits.SignIn).WithName("Login")
            .WithDescription("Starts a sign-in session (cookie) for /connect/authorize. The tenant comes from the host or header, else the default.");
        auth.MapPost("/logout", (Delegate)SessionLogoutAsync).WithName("Logout");
    }

    public static string Issuer(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    private static Ok<DiscoveryDocument> Discovery(HttpRequest request)
    {
        var issuer = Issuer(request);
        return TypedResults.Ok(new DiscoveryDocument(
            issuer,
            $"{issuer}/connect/authorize",
            $"{issuer}/connect/token",
            $"{issuer}/connect/userinfo",
            $"{issuer}/connect/logout",
            $"{issuer}/.well-known/jwks",
            ["code"],
            [OAuth.AuthorizationCode, OAuth.RefreshToken, OAuth.ClientCredentials, OAuth.Password],
            ["S256"],
            [OAuth.OpenId, OAuth.Profile, OAuth.OfflineAccess, OAuth.Api],
            ["public"],
            [SecurityAlgorithms.RsaSha256],
            ["client_secret_basic", "client_secret_post", "none"]));
    }

    private static async Task<Ok<JsonWebKeySetResponse>> JwksAsync(ServerKeys keys, CancellationToken cancellationToken)
    {
        var key = await keys.SigningKeyAsync(cancellationToken);
        var parameters = key.Rsa.ExportParameters(includePrivateParameters: false);
        return TypedResults.Ok(new JsonWebKeySetResponse(
            [new JsonWebKeyData("RSA", ServerKeys.Signing, key.KeyId, SecurityAlgorithms.RsaSha256, OAuth.Base64UrlText(parameters.Modulus!), OAuth.Base64UrlText(parameters.Exponent!))]));
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http, IdentityDbContext db, TenantResolver tenants, ReverseProxySignIn proxy, IOptions<AuthOptions> options, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var parameters = http.Request.HasFormContentType ? (await http.Request.ReadFormAsync(cancellationToken)).ToDictionary() : http.Request.Query.ToDictionary();
        string? Parameter(string name) => parameters.TryGetValue(name, out var value) && value.ToString() is { Length: > 0 } text ? text : null;

        // Until the client and its redirect URI are known, errors cannot go back to the client.
        var tenant = await tenants.ResolveOrDefaultAsync(http.Request, cancellationToken);
        if (tenant.Id is not { } tenantId)
        {
            return Problem("invalid_request", "No active tenant matches this request.");
        }

        if (Parameter("client_id") is not { } clientId || await Applications.FindAsync(db, tenantId, clientId, options.Value, cancellationToken) is not { } client)
        {
            return Problem("invalid_client", "Unknown client.");
        }

        if (Parameter("redirect_uri") is not { } redirectUri || !OAuth.Split(client.RedirectUris).Contains(redirectUri, StringComparer.Ordinal))
        {
            return Problem("invalid_request", "The redirect_uri is not registered for this client.");
        }

        var state = Parameter("state");
        IResult Error(string error, string description) => Results.Redirect(WithQuery(redirectUri, ("error", error), ("error_description", description), ("state", state)));

        if (Parameter("response_type") != "code")
        {
            return Error("unsupported_response_type", "Only response_type=code is supported.");
        }

        if (!OAuth.Split(client.GrantTypes).Contains(OAuth.AuthorizationCode))
        {
            return Error("unauthorized_client", "The client may not use the authorization code grant.");
        }

        if (Parameter("code_challenge") is not { Length: >= 43 and <= 128 } challenge || Parameter("code_challenge_method") != "S256")
        {
            return Error("invalid_request", "PKCE is required: code_challenge with code_challenge_method=S256.");
        }

        var scopes = OAuth.Split(Parameter("scope") ?? "");
        var allowed = OAuth.Split(client.Scopes);
        if (scopes.FirstOrDefault(s => !allowed.Contains(s)) is { } notAllowed)
        {
            return Error("invalid_scope", $"The client may not request the scope '{notAllowed}'.");
        }

        var prompt = OAuth.Split(Parameter("prompt") ?? "");
        var user = prompt.Contains("login") ? null : await SignInSession.UserAsync(http, db, tenantId);

        // An authenticating reverse proxy (IAM-15) vouches for the user; its identity replaces the session's.
        if (await proxy.AuthenticateAsync(http, tenantId, cancellationToken) is { } proxied)
        {
            await SignInSession.SignInAsync(http, proxied, "proxy");
            user = proxied;
        }
        if (user is null)
        {
            if (prompt.Contains("none"))
            {
                return Error("login_required", "The user is not signed in.");
            }

            if (options.Value.LoginUrl is { Length: > 0 } loginUrl)
            {
                return Results.Redirect(WithQuery(loginUrl, ("returnUrl", $"{http.Request.PathBase}{http.Request.Path}{QueryOf(parameters)}")));
            }

            return Results.Unauthorized();
        }

        var code = OAuth.Base64UrlText(RandomNumberGenerator.GetBytes(32));
        db.OAuthCodes.Add(new OAuthCode
        {
            Id = Ids.New(),
            TenantId = tenantId,
            CodeHash = OAuth.HashSecret(code),
            ClientId = client.ClientId,
            UserId = user.Id,
            RedirectUri = redirectUri,
            CodeChallenge = challenge,
            Scope = OAuth.Join(scopes),
            Nonce = Parameter("nonce") is { Length: <= 256 } nonce ? nonce : null,
            SecurityStamp = user.SecurityStamp,
            ExpiresAt = (time.GetUtcNow() + CodeLifetime).ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Redirect(WithQuery(redirectUri, ("code", code), ("state", state), ("iss", Issuer(http.Request))));
    }

    /// <summary>Whether <paramref name="verifier"/> matches the S256 <paramref name="challenge"/> (RFC 7636).</summary>
    public static bool VerifyPkce(string? verifier, string challenge)
    {
        if (verifier is not { Length: >= 43 and <= 128 })
        {
            return false;
        }

        var computed = OAuth.Base64UrlText(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    /// <summary>A signed identity token for <paramref name="clientId"/>.</summary>
    public static async Task<string> IdTokenAsync(
        ServerKeys keys, HttpRequest request, User user, string tenantIdentifier, string clientId, string? nonce, TimeSpan lifetime, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var claims = new Dictionary<string, object>
        {
            [PaperDotNetClaims.UserId] = user.Id.ToString(),
            ["name"] = user.DisplayName,
            ["preferred_username"] = user.UserName,
            [PaperDotNetClaims.TenantId] = user.TenantId.ToString(),
            ["tenant"] = tenantIdentifier,
        };
        if (nonce is not null)
        {
            claims["nonce"] = nonce;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer(request),
            Audience = clientId,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + lifetime).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(await keys.SigningKeyAsync(cancellationToken), SecurityAlgorithms.RsaSha256),
        });
    }

    private static async Task<Results<Ok<UserInfoResponse>, UnauthorizedHttpResult>> UserInfoAsync(Caller caller, IdentityDbContext db, CancellationToken cancellationToken)
    {
        if (await Users.FindAsync(db, caller.TenantId, caller.UserId, cancellationToken) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        var database = db;
        var tenantId = caller.TenantId;
        var ct = cancellationToken;
        var identifier = await database.Tenants.Where(t => t.Id == tenantId).Select(t => t.Identifier).FirstOrDefaultAsync(ct) ?? "";
        return TypedResults.Ok(new UserInfoResponse(user.Id.ToString(), user.DisplayName, user.UserName, user.Email, user.TenantId.ToString(), identifier));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http, IdentityDbContext db, TenantResolver tenants, IOptions<AuthOptions> options, CancellationToken cancellationToken)
    {
        await http.SignOutAsync(SignInSession.Scheme);
        var query = http.Request.Query;
        if (query["post_logout_redirect_uri"].ToString() is { Length: > 0 } target && query["client_id"].ToString() is { Length: > 0 } clientId
            && (await tenants.ResolveOrDefaultAsync(http.Request, cancellationToken)).Id is { } tenantId
            && await Applications.FindAsync(db, tenantId, clientId, options.Value, cancellationToken) is { } client
            && OAuth.Split(client.PostLogoutRedirectUris).Contains(target, StringComparer.Ordinal))
        {
            return Results.Redirect(WithQuery(target, ("state", query["state"].ToString() is { Length: > 0 } state ? state : null)));
        }

        return Results.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> LoginAsync(
        LoginRequest request, HttpContext http, IdentityDbContext db, IPasswordHasher<User> hasher, TenantResolver tenants, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenant = await tenants.ResolveOrDefaultAsync(http.Request, cancellationToken);
        var user = tenant.Id is null || string.IsNullOrEmpty(request.UserName) || string.IsNullOrEmpty(request.Password)
            ? null
            : await Users.FindForSignInAsync(db, tenant.Identifier, request.UserName, cancellationToken);
        if (user is null || !SignInSession.CanSignIn(user) || !await TokenEndpoint.CheckPasswordAsync(db, hasher, user, request.Password!, time.GetUtcNow(), cancellationToken))
        {
            return ApiErrors.Problem(StatusCodes.Status401Unauthorized, "invalidCredentials", "The user name or password is not correct.");
        }

        await SignInSession.SignInAsync(http, user, "pwd");
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SessionLogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(SignInSession.Scheme);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult Problem(string error, string description) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: error, detail: description);

    private static string QueryOf(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> parameters) =>
        "?" + string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value.ToString())}"));

    /// <summary><paramref name="uri"/> with the parameters that have a value added to its query.</summary>
    private static string WithQuery(string uri, params (string Name, string? Value)[] parameters)
    {
        var builder = new StringBuilder(uri);
        var separator = uri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        foreach (var (name, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(separator).Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }
}

/// <summary>Removes authorization codes that expired unused.</summary>
internal sealed class OAuthCodeCleanupJob(IdentityDbContext db, TimeProvider time) : PaperDotNet.Jobs.Contracts.ITenantRecurringJob
{
    public const string Name = "identity.oauthCodes";
    public const string Schedule = "17 * * * *";

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = tenantId;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var ct = cancellationToken;
        var expired = await database.OAuthCodes.Where(c => c.TenantId == tenant && c.ExpiresAt < now).ToListAsync(ct);
        if (expired.Count > 0)
        {
            database.OAuthCodes.RemoveRange(expired);
            await database.SaveChangesAsync(ct);
        }
    }
}
