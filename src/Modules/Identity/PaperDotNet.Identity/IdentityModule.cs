using System.Text.Json.Serialization.Metadata;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;
using PaperDotNet.Identity.Features;
using PaperDotNet.Persistence;

namespace PaperDotNet.Identity;

/// <summary>
/// Tenants, users and sign-in (ADR-0039 slice of Identity and Tenancy): bearer tokens protected with Data Protection
/// (keys in <c>{Storage:DataPath}/keys</c>), password hashing from ASP.NET Core Identity, a rate limit on sign-in.
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
        services.AddScoped<TenantProvisioner>();
        services.AddSingleton<TokenIssuer>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddDataProtection()
            .SetApplicationName("PaperDotNet")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
        services.AddAuthentication(BearerTokenDefaults.AuthenticationScheme)
            .AddBearerToken(options =>
            {
                options.BearerTokenExpiration = auth.AccessTokenLifetime;
                options.RefreshTokenExpiration = auth.RefreshTokenLifetime;
            });
        services.AddAuthorization();
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
        Users.Map(endpoints);
    }
}
