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
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Identity;

/// <summary>
/// Tenants, users, groups (nested), roles and sign-in (Identity and, for now, Tenancy): OAuth 2.0 and OpenID Connect
/// (authorization code with PKCE through a sign-in session, client credentials, password and refresh grants, registered
/// clients), bearer tokens protected with Data Protection (keys in <c>{Storage:DataPath}/keys</c>), password hashing
/// from ASP.NET Core Identity, a rate limit and lockout on sign-in, scopes from roles checked on every request.
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
        services.AddSingleton<TenantResolver>();
        services.AddScoped<IEffectiveScopeProvider, EffectiveScopes>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IRoleProvisioning, RoleProvisioning>();
        services.AddScoped<IUserPreferences, UserPreferences>();
        services.AddScoped<ITemplateHandler>(sp => new GroupTemplateHandler(sp.GetRequiredService<IdentityDbContext>()));
        services.AddScoped<ITemplateHandler>(sp => new RoleTemplateHandler(sp.GetRequiredService<IdentityDbContext>(), sp.GetRequiredService<IScopeCatalog>()));
        services.AddSingleton<TokenIssuer>();
        services.AddSingleton<ServerKeys>();
        services.AddTenantRecurringJob<OAuthCodeCleanupJob>(OAuthCodeCleanupJob.Name, OAuthCodeCleanupJob.Schedule);
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
            })
            .AddCookie(SignInSession.Scheme, options =>
            {
                // The sign-in session of /connect/authorize: never a redirect of its own, the endpoint decides.
                options.Cookie.Name = "pdn.session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = auth.SessionLifetime;
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        // Sign-ins per client address and minute (Identity:SignInsPerMinute, default 20).
        var signInsPerMinute = int.TryParse(configuration["Identity:SignInsPerMinute"], System.Globalization.CultureInfo.InvariantCulture, out var limit) && limit > 0 ? limit : 20;
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimits.SignIn, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = signInsPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        TokenEndpoint.Map(endpoints);
        OpenIdConnect.Map(endpoints);
        Applications.Map(endpoints);
        Me.Map(endpoints);
        Users.Map(endpoints);
        Groups.Map(endpoints);
        Roles.Map(endpoints);
        PreferencesEndpoints.Map(endpoints);
    }
}

public static class TenantGuardExtensions
{
    /// <summary>Rejects API requests whose host or header names an unknown tenant or another tenant than the token's. Call after authentication.</summary>
    public static IApplicationBuilder UsePaperDotNetTenantGuard(this IApplicationBuilder app) => app.UseMiddleware<TenantGuardMiddleware>();
}
