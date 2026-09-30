using System.Text.Json.Serialization.Metadata;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;
using PaperDotNet.Identity.Features;
using PaperDotNet.Persistence;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Identity;

/// <summary>
/// Tenants, users, groups (nested), roles and sign-in (Identity and, for now, Tenancy): bearer tokens protected with
/// Data Protection (keys in <c>{Storage:DataPath}/keys</c>), password hashing from ASP.NET Core Identity, a rate limit
/// and lockout on sign-in, scopes from roles checked on every request.
/// </summary>
public sealed class IdentityModule : IModule
{
    public string Name => "Identity";

    public IJsonTypeInfoResolver Json => IdentityJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        services.Configure<TenancyOptions>(configuration.GetSection("Tenancy"));
        services.Configure<BootstrapOptions>(configuration.GetSection("Bootstrap"));
        var auth = configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        var dataPath = Path.GetFullPath(configuration["Storage:DataPath"] is { Length: > 0 } path ? path : "data");

        services.AddModuleDbContext<IdentityDbContext>();
        services.AddScopes(IdentityScopes.All);
        services.AddMemoryCache();
        services.AddScoped<TenantProvisioner>();
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        services.AddScoped<IEffectiveScopeProvider, EffectiveScopes>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IRoleProvisioning, RoleProvisioning>();
        services.AddScoped<IUserPreferences, UserPreferences>();
        services.AddSingleton<TokenIssuer>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddDataProtection()
            .SetApplicationName("PaperDotNet")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
        services.AddAuthentication(PaperDotNetAuthenticationHandler.SchemeName)
            .AddBearerToken(options =>
            {
                options.BearerTokenExpiration = auth.AccessTokenLifetime;
                options.RefreshTokenExpiration = auth.RefreshTokenLifetime;
            })
            .AddScheme<AuthenticationSchemeOptions, PaperDotNetAuthenticationHandler>(PaperDotNetAuthenticationHandler.SchemeName, options =>
            {
                options.ForwardChallenge = BearerTokenDefaults.AuthenticationScheme;
                options.ForwardForbid = BearerTokenDefaults.AuthenticationScheme;
            });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimits.SignIn, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        TokenEndpoint.Map(endpoints);
        Me.Map(endpoints);
        Users.Map(endpoints);
        Groups.Map(endpoints);
        Roles.Map(endpoints);
        PreferencesEndpoints.Map(endpoints);
    }
}
