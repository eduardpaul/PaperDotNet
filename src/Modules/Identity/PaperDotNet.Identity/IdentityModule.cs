using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;
using PaperDotNet.Identity.Features;
using PaperDotNet.Persistence;

namespace PaperDotNet.Identity;

public sealed class IdentityModule : IModule
{
    public string Name => "Identity";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddHttpContextAccessor();
        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.Schema);

        services.AddIdentityCore<User>(o =>
            {
                o.User.RequireUniqueEmail = false;
                // Length over composition rules (NIST SP 800-63B).
                o.Password.RequiredLength = 10;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireDigit = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 10;
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3; // passkeys
            })
            .AddEntityFrameworkStores<IdentityDbContext>();

        services.AddScoped<IPasskeyHandler<User>, PasskeyHandler<User>>();
        services.AddOptions<IdentityPasskeyOptions>().Configure<Microsoft.Extensions.Options.IOptions<AuthOptions>>((passkeys, auth) =>
        {
            passkeys.ServerDomain = auth.Value.PasskeyServerDomain;
            if (auth.Value.PasskeyOrigins.Count > 0)
            {
                var origins = auth.Value.PasskeyOrigins.ToHashSet(StringComparer.OrdinalIgnoreCase);
                passkeys.ValidateOrigin = context => ValueTask.FromResult(!context.CrossOrigin && origins.Contains(context.Origin));
            }
        });
        services.AddSingleton<PasskeyState>();

        services.AddPaperDotNetAuthentication();
        services.AddHostedService<FirstPartyClientSync>();
        services.AddScoped<IEffectiveScopeProvider, EffectiveScopeProvider>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<ITenantInitializer, IdentityTenantInitializer>();
        services.AddScopes(IdentityScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        AuthEndpoints.Map(endpoints);
        OAuthEndpoints.Map(endpoints);
        ApplicationEndpoints.Map(endpoints);
        MeEndpoints.Map(endpoints);
        DirectoryEndpoints.Map(endpoints);
    }
}
