using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record ApplicationResponse(
    Guid Id,
    string ClientId,
    string DisplayName,
    string ClientType,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris,
    Guid? ServiceUserId,
    bool IsFirstParty);

/// <summary>
/// A new OAuth client. <c>clientType</c>: <c>confidential</c> (has a secret) or <c>public</c> (browser and native apps,
/// PKCE only). <c>grantTypes</c>: <c>authorization_code</c>, <c>refresh_token</c>, <c>client_credentials</c>
/// (confidential only: the client acts as its own service account). <c>scopes</c>: permission scopes, <c>api</c> for
/// full access as the user, and <c>openid</c>, <c>profile</c>, <c>offline_access</c>.
/// </summary>
public sealed record CreateApplicationRequest(
    string? DisplayName,
    string? ClientType,
    IReadOnlyList<string>? GrantTypes,
    IReadOnlyList<string>? Scopes,
    IReadOnlyList<string>? RedirectUris,
    IReadOnlyList<string>? PostLogoutRedirectUris);

/// <summary>Returned once, at creation or rotation: the only time the secret is visible.</summary>
public sealed record ApplicationSecretResponse(ApplicationResponse Application, string? ClientSecret);

/// <summary>OAuth grant types and scopes besides the permission scopes.</summary>
internal static class OAuth
{
    public const string AuthorizationCode = "authorization_code";
    public const string RefreshToken = "refresh_token";
    public const string ClientCredentials = "client_credentials";
    public const string Password = "password";

    /// <summary>Full access as the user (first-party apps); without it a token is limited to the permission scopes it was granted.</summary>
    public const string Api = "api";
    public const string OpenId = "openid";
    public const string Profile = "profile";
    public const string OfflineAccess = "offline_access";

    public static IReadOnlyList<string> Split(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string Join(IEnumerable<string> values) => string.Join(' ', values.Distinct(StringComparer.Ordinal));

    public static string HashSecret(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static string NewSecret() => Base64UrlText(RandomNumberGenerator.GetBytes(32));

    public static string Base64UrlText(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}

/// <summary>
/// The OAuth clients of a tenant (IAM-02), trusted by it: there is no consent screen. The first-party client
/// <c>paperdotnet</c> (password grant for the CLI and scripts, authorization code for the web UI at
/// <c>Auth:FirstPartyRedirectUris</c>, full access) exists in every tenant without a row.
/// </summary>
internal static class Applications
{
    private static readonly HashSet<string> AllowedGrants = new([OAuth.AuthorizationCode, OAuth.RefreshToken, OAuth.ClientCredentials], StringComparer.Ordinal);

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/applications").WithTags("Applications");
        group.MapGet("", ListAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("ListApplications");
        group.MapPost("", CreateAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("CreateApplication");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("GetApplication");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("DeleteApplication");
        group.MapPost("/{id:guid}/secret", RotateSecretAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("RotateApplicationSecret")
            .WithDescription("A new secret for a confidential client; the old one stops working.");
    }

    /// <summary>The first-party client: public, password and refresh grants, authorization code when the web UI's redirect URIs are configured.</summary>
    public static OAuthClient FirstParty(Guid tenantId, AuthOptions options) => new()
    {
        Id = Guid.Empty,
        TenantId = tenantId,
        ClientId = OAuthClient.FirstPartyClientId,
        DisplayName = "PaperDotNet",
        ClientType = OAuthClientTypes.Public,
        GrantTypes = OAuth.Join([OAuth.Password, OAuth.RefreshToken, .. options.FirstPartyRedirectUris.Count > 0 ? [OAuth.AuthorizationCode] : Array.Empty<string>()]),
        Scopes = OAuth.Join([OAuth.Api, OAuth.OpenId, OAuth.Profile, OAuth.OfflineAccess]),
        RedirectUris = OAuth.Join(options.FirstPartyRedirectUris),
        PostLogoutRedirectUris = OAuth.Join(options.FirstPartyRedirectUris),
    };

    /// <summary>A client of the tenant by its client id (the first-party client included), or null.</summary>
    public static async Task<OAuthClient?> FindAsync(IdentityDbContext database, Guid tenantId, string clientId, AuthOptions options, CancellationToken cancellationToken)
    {
        if (clientId == OAuthClient.FirstPartyClientId)
        {
            return FirstParty(tenantId, options);
        }

        var db = database;
        var tenant = tenantId;
        var id = clientId;
        var ct = cancellationToken;
        return await db.OAuthClients.AsNoTracking().Where(c => c.TenantId == tenant && c.ClientId == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Whether the client authenticated: a public client always, a confidential one with its secret.</summary>
    public static bool Authenticates(OAuthClient client, string? secret)
    {
        if (client.ClientType != OAuthClientTypes.Confidential)
        {
            return true;
        }

        return secret is { Length: > 0 } && client.SecretHash is { } hash
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(OAuth.HashSecret(secret)), Encoding.ASCII.GetBytes(hash));
    }

    public static ApplicationResponse ToResponse(OAuthClient c) => new(
        c.Id, c.ClientId, c.DisplayName, c.ClientType, OAuth.Split(c.GrantTypes), OAuth.Split(c.Scopes), OAuth.Split(c.RedirectUris),
        OAuth.Split(c.PostLogoutRedirectUris), c.ServiceUserId, c.ClientId == OAuthClient.FirstPartyClientId);

    private static async Task<Ok<List<ApplicationResponse>>> ListAsync(Caller caller, IdentityDbContext db, IOptions<AuthOptions> options, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var clients = await database.OAuthClients.AsNoTracking().Where(c => c.TenantId == tenant).OrderBy(c => c.ClientId).ToListAsync(ct);
        return TypedResults.Ok(clients.Prepend(FirstParty(tenant, options.Value)).Select(ToResponse).ToList());
    }

    private static async Task<Results<Ok<ApplicationResponse>, ProblemHttpResult>> GetAsync(Guid id, Caller caller, IdentityDbContext db, CancellationToken cancellationToken) =>
        await FindByIdAsync(db, caller.TenantId, id, tracking: false, cancellationToken) is { } client ? TypedResults.Ok(ToResponse(client)) : ApiErrors.NotFound();

    private static async Task<Results<Created<ApplicationSecretResponse>, ValidationProblem>> CreateAsync(
        CreateApplicationRequest request, Caller caller, IdentityDbContext db, IScopeCatalog catalog, TimeProvider time, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.DisplayName?.Trim() ?? "";
        if (name.Length is 0 or > 200)
        {
            errors["displayName"] = ["A name of 1 to 200 characters is required."];
        }

        var confidential = request.ClientType == OAuthClientTypes.Confidential;
        if (!confidential && request.ClientType != OAuthClientTypes.Public)
        {
            errors["clientType"] = ["confidential or public is expected."];
        }

        var grants = (request.GrantTypes ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (grants.Count == 0 || grants.Any(g => !AllowedGrants.Contains(g)))
        {
            errors["grantTypes"] = [$"One or more of: {string.Join(", ", AllowedGrants)}."];
        }
        else if (grants.Contains(OAuth.ClientCredentials) && !confidential)
        {
            errors["grantTypes"] = ["client_credentials needs a confidential client."];
        }

        var redirectUris = (request.RedirectUris ?? []).Distinct(StringComparer.Ordinal).ToList();
        var logoutUris = (request.PostLogoutRedirectUris ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (grants.Contains(OAuth.AuthorizationCode) && redirectUris.Count == 0)
        {
            errors["redirectUris"] = ["authorization_code needs at least one redirect URI."];
        }

        if (redirectUris.Concat(logoutUris).Any(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Fragment.Length > 0 || u.Contains(' ', StringComparison.Ordinal)))
        {
            errors["redirectUris"] = ["Redirect URIs must be absolute URIs without a fragment."];
        }

        var scopes = (request.Scopes ?? []).Distinct(StringComparer.Ordinal).ToList();
        var unknown = scopes.Where(s => s is not (OAuth.Api or OAuth.OpenId or OAuth.Profile or OAuth.OfflineAccess) && !catalog.Contains(s)).ToList();
        if (unknown.Count > 0)
        {
            errors["scopes"] = [$"Unknown scopes: {string.Join(", ", unknown)}."];
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var now = time.GetUtcNow();
        var secret = confidential ? OAuth.NewSecret() : null;
        var client = new OAuthClient
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            ClientId = $"pdn-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10))}",
            DisplayName = name,
            ClientType = request.ClientType!,
            SecretHash = secret is null ? null : OAuth.HashSecret(secret),
            GrantTypes = OAuth.Join(grants),
            Scopes = OAuth.Join(scopes),
            RedirectUris = OAuth.Join(redirectUris),
            PostLogoutRedirectUris = OAuth.Join(logoutUris),
            CreatedAt = now,
        };
        if (grants.Contains(OAuth.ClientCredentials))
        {
            // The client acts as this account: give it roles or workspace membership like any user.
            var account = Users.New(caller.TenantId, client.ClientId, name, null, now);
            account.IsServiceAccount = true;
            db.Users.Add(account);
            client.ServiceUserId = account.Id;
        }

        db.OAuthClients.Add(client);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v1.0/applications/{client.Id}", new ApplicationSecretResponse(ToResponse(client), secret));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, Caller caller, IdentityDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await FindByIdAsync(db, caller.TenantId, id, tracking: true, cancellationToken) is not { } client)
        {
            return ApiErrors.NotFound();
        }

        if (client.ServiceUserId is { } serviceUserId && await Users.FindAsync(db, caller.TenantId, serviceUserId, cancellationToken) is { } account)
        {
            // The account stays (it may own content), but can no longer act.
            account.IsDisabled = true;
            await Users.EndSessionsAsync(db, account, revokeApiTokens: true, time.GetUtcNow(), cancellationToken);
        }

        db.OAuthClients.Remove(client);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ApplicationSecretResponse>, ProblemHttpResult>> RotateSecretAsync(
        Guid id, Caller caller, IdentityDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await FindByIdAsync(db, caller.TenantId, id, tracking: true, cancellationToken) is not { } client)
        {
            return ApiErrors.NotFound();
        }

        if (client.ClientType != OAuthClientTypes.Confidential)
        {
            return ApiErrors.Conflict("publicClient", "Public clients have no secret.");
        }

        var secret = OAuth.NewSecret();
        client.SecretHash = OAuth.HashSecret(secret);
        if (client.ServiceUserId is { } serviceUserId && await Users.FindAsync(db, caller.TenantId, serviceUserId, cancellationToken) is { } account)
        {
            // Tokens issued with the old secret end too.
            await Users.EndSessionsAsync(db, account, revokeApiTokens: false, time.GetUtcNow(), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new ApplicationSecretResponse(ToResponse(client), secret));
    }

    private static Task<OAuthClient?> FindByIdAsync(IdentityDbContext database, Guid tenantId, Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var clientId = id;
        var ct = cancellationToken;
        return tracking
            ? db.OAuthClients.Where(c => c.TenantId == tenant && c.Id == clientId).FirstOrDefaultAsync(ct)
            : db.OAuthClients.AsNoTracking().Where(c => c.TenantId == tenant && c.Id == clientId).FirstOrDefaultAsync(ct);
    }
}
