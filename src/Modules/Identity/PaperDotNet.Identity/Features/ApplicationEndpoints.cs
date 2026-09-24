using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Data;
using PaperDotNet.Tenancy.Contracts;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace PaperDotNet.Identity.Features;

public sealed record ApplicationResponse(
    Guid Id,
    string ClientId,
    string? DisplayName,
    string ClientType,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris,
    Guid? ServiceUserId,
    bool IsFirstParty);

/// <summary>
/// A new OAuth client. <c>clientType</c>: <c>confidential</c> (has a secret) or <c>public</c>
/// (native/SPA, PKCE only). <c>grantTypes</c>: <c>authorization_code</c>, <c>refresh_token</c>,
/// <c>client_credentials</c> (confidential only; the client acts as its own service account).
/// <c>scopes</c>: permission scopes, or <c>api</c> for full access as the user.
/// </summary>
public sealed record CreateApplicationRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string DisplayName,
    [property: Required] string ClientType,
    [property: Required, MinLength(1)] IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string>? Scopes,
    IReadOnlyList<string>? RedirectUris,
    IReadOnlyList<string>? PostLogoutRedirectUris);

/// <summary>Returned once, at creation or rotation: the only time the secret is visible.</summary>
public sealed record ApplicationSecretResponse(ApplicationResponse Application, string? ClientSecret);

/// <summary>OAuth client registration (IAM-02). Clients belong to the tenant and are trusted by it (no consent screen).</summary>
internal static class ApplicationEndpoints
{
    private static readonly HashSet<string> AllowedGrants = [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken, GrantTypes.ClientCredentials];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("applications", "Applications");
        group.MapGet("", ListAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("ListApplications");
        group.MapPost("", CreateAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("CreateApplication");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("GetApplication");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("DeleteApplication");
        group.MapPost("/{id:guid}/secret", RotateSecretAsync).RequireScope(IdentityScopes.ApplicationManage).WithName("RotateApplicationSecret");
    }

    private static async Task<Ok<List<ApplicationResponse>>> ListAsync(IdentityDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.Applications.AsNoTracking().OrderBy(a => a.ClientId).ToListAsync(ct)).Select(ToResponse).ToList());

    private static async Task<Results<Ok<ApplicationResponse>, ProblemHttpResult>> GetAsync(Guid id, IdentityDbContext db, CancellationToken ct) =>
        await db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } application
            ? TypedResults.Ok(ToResponse(application))
            : ApiErrors.NotFound();

    private static async Task<Results<Created<ApplicationSecretResponse>, ValidationProblem>> CreateAsync(
        CreateApplicationRequest request, IOpenIddictApplicationManager applications, UserManager<User> users, IScopeCatalog catalog, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var errors = new Dictionary<string, string[]>();
        var confidential = request.ClientType == ClientTypes.Confidential;
        if (!confidential && request.ClientType != ClientTypes.Public)
        {
            errors["clientType"] = ["confidential or public is expected."];
        }

        var grants = request.GrantTypes.Distinct(StringComparer.Ordinal).ToList();
        if (grants.Any(g => !AllowedGrants.Contains(g)))
        {
            errors["grantTypes"] = [$"Allowed: {string.Join(", ", AllowedGrants)}."];
        }
        else if (grants.Contains(GrantTypes.ClientCredentials) && !confidential)
        {
            errors["grantTypes"] = ["client_credentials needs a confidential client."];
        }

        var redirectUris = request.RedirectUris ?? [];
        if (grants.Contains(GrantTypes.AuthorizationCode) && redirectUris.Count == 0)
        {
            errors["redirectUris"] = ["authorization_code needs at least one redirect URI."];
        }

        if (redirectUris.Concat(request.PostLogoutRedirectUris ?? []).Any(u => !Uri.TryCreate(u, UriKind.Absolute, out _)))
        {
            errors["redirectUris"] = ["Redirect URIs must be absolute URIs."];
        }

        var known = catalog.All.Select(s => s.Name).Concat([OAuthScopes.Api, Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess]).ToHashSet(StringComparer.Ordinal);
        var scopes = (request.Scopes ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Any(s => !known.Contains(s)))
        {
            errors["scopes"] = [$"Unknown scopes: {string.Join(", ", scopes.Where(s => !known.Contains(s)))}."];
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var clientId = $"pdn-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10))}";
        var secret = confidential ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null;
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = request.ClientType,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = request.DisplayName.Trim(),
        };
        AddPermissions(descriptor, grants, scopes);
        descriptor.RedirectUris.UnionWith(redirectUris.Select(u => new Uri(u)));
        descriptor.PostLogoutRedirectUris.UnionWith((request.PostLogoutRedirectUris ?? []).Select(u => new Uri(u)));
        var application = (OAuthApplication)await applications.CreateAsync(descriptor, ct);

        if (grants.Contains(GrantTypes.ClientCredentials))
        {
            // The client acts as this account: give it roles or workspace membership like any user.
            var account = new User { Id = Ids.New(), UserName = clientId, DisplayName = descriptor.DisplayName, IsServiceAccount = true };
            var created = await users.CreateAsync(account);
            if (!created.Succeeded)
            {
                await applications.DeleteAsync(application, ct);
                return ApiErrors.Validation(new Dictionary<string, string[]> { ["serviceAccount"] = [.. created.Errors.Select(e => e.Description)] });
            }

            application.ServiceUserId = account.Id;
            await applications.UpdateAsync(application, ct);
        }

        var response = ToResponse(application);
        return TypedResults.Created($"{ApiRoutes.V1}/applications/{application.Id}", new ApplicationSecretResponse(response, secret));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, IdentityDbContext db, IOpenIddictApplicationManager applications, UserManager<User> users, CancellationToken ct)
    {
        var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (application is null)
        {
            return ApiErrors.NotFound();
        }

        if (application.ClientId == OAuthApplication.FirstPartyClientId)
        {
            return ApiErrors.Conflict("firstPartyClient", "The first-party client cannot be deleted.");
        }

        if (application.ServiceUserId is { } serviceUserId && await users.FindByIdAsync(serviceUserId.ToString()) is { } account)
        {
            account.IsDisabled = true;
            await users.UpdateAsync(account);
        }

        await applications.DeleteAsync(application, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ApplicationSecretResponse>, ProblemHttpResult>> RotateSecretAsync(
        Guid id, IdentityDbContext db, IOpenIddictApplicationManager applications, CancellationToken ct)
    {
        var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (application is null)
        {
            return ApiErrors.NotFound();
        }

        if (application.ClientType != ClientTypes.Confidential)
        {
            return ApiErrors.Conflict("publicClient", "Public clients have no secret.");
        }

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await applications.UpdateAsync(application, secret, ct);
        return TypedResults.Ok(new ApplicationSecretResponse(ToResponse(application), secret));
    }

    internal static void AddPermissions(OpenIddictApplicationDescriptor descriptor, IReadOnlyCollection<string> grants, IReadOnlyCollection<string> scopes)
    {
        descriptor.Permissions.UnionWith([Permissions.Endpoints.Token, Permissions.Endpoints.Revocation]);
        if (grants.Contains(GrantTypes.AuthorizationCode))
        {
            descriptor.Permissions.UnionWith([Permissions.Endpoints.Authorization, Permissions.Endpoints.EndSession, Permissions.ResponseTypes.Code]);
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        }

        descriptor.Permissions.UnionWith(grants.Select(g => Permissions.Prefixes.GrantType + g));
        descriptor.Permissions.UnionWith(scopes.Select(s => Permissions.Prefixes.Scope + s));
    }

    private static ApplicationResponse ToResponse(OAuthApplication a)
    {
        var permissions = Json(a.Permissions);
        return new ApplicationResponse(
            a.Id,
            a.ClientId!,
            a.DisplayName,
            a.ClientType ?? ClientTypes.Public,
            permissions.Where(p => p.StartsWith(Permissions.Prefixes.GrantType, StringComparison.Ordinal)).Select(p => p[Permissions.Prefixes.GrantType.Length..]).ToList(),
            permissions.Where(p => p.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal)).Select(p => p[Permissions.Prefixes.Scope.Length..]).ToList(),
            Json(a.RedirectUris),
            Json(a.PostLogoutRedirectUris),
            a.ServiceUserId,
            a.ClientId == OAuthApplication.FirstPartyClientId);
    }

    private static List<string> Json(string? array) =>
        string.IsNullOrEmpty(array) ? [] : JsonSerializer.Deserialize<List<string>>(array) ?? [];
}

/// <summary>
/// The built-in public client <c>paperdotnet</c> of every tenant: password grant (CLI,
/// scripts), authorization code + PKCE for the first-party UI, refresh tokens, full access.
/// </summary>
internal static class FirstPartyClient
{
    public static async Task EnsureAsync(IOpenIddictApplicationManager applications, AuthOptions options, CancellationToken ct)
    {
        var redirectUris = options.FirstPartyRedirectUris.Select(u => new Uri(u)).ToList();
        var existing = await applications.FindByClientIdAsync(OAuthApplication.FirstPartyClientId, ct);
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = OAuthApplication.FirstPartyClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "PaperDotNet",
        };
        ApplicationEndpoints.AddPermissions(
            descriptor,
            [GrantTypes.Password, GrantTypes.RefreshToken, .. redirectUris.Count > 0 ? [GrantTypes.AuthorizationCode] : Array.Empty<string>()],
            [OAuthScopes.Api, Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess]);
        descriptor.RedirectUris.UnionWith(redirectUris);
        descriptor.PostLogoutRedirectUris.UnionWith(redirectUris);
        if (existing is null)
        {
            await applications.CreateAsync(descriptor, ct);
        }
        else
        {
            // Keeps permissions and redirect URIs in line with the configuration.
            await applications.UpdateAsync(existing, descriptor, ct);
        }
    }
}

/// <summary>Brings the first-party client of every active tenant in line with the configuration at startup.</summary>
internal sealed partial class FirstPartyClientSync(
    IServiceScopeFactory scopes, ITenantScopeFactory tenantScopes, IHostApplicationLifetime lifetime, IOptions<AuthOptions> options, ILogger<FirstPartyClientSync> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // After the host started: startup migrations have run.
        var started = new TaskCompletionSource();
        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
        {
            try
            {
                await started.Task;
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }

        try
        {
            IReadOnlyList<TenantSummary> tenants;
            await using (var scope = scopes.CreateAsyncScope())
            {
                tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(stoppingToken);
            }

            foreach (var tenant in tenants.Where(t => t.Status == TenantStatus.Active))
            {
                await using var scope = tenantScopes.CreateScope(tenant.Id, tenant.Identifier);
                await FirstPartyClient.EnsureAsync(scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(), options.Value, stoppingToken);
            }
        }
#pragma warning disable CA1031 // A failed sync must not stop the host; tenants get the client on creation anyway.
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            LogSyncFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not sync the first-party OAuth client.")]
    private partial void LogSyncFailed(Exception exception);
}
