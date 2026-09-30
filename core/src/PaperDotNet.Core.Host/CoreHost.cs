using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Core.Host.Data;
using PaperDotNet.Core.Host.Persistence.Sqlite;
using PaperDotNet.Core.Messaging;
using PaperDotNet.Core.Persistence;
using Wolverine;
using Wolverine.Sqlite;

namespace PaperDotNet.Core.Host;

/// <summary>Service registration and the HTTP pipeline of the AOT core (ADR-0039).</summary>
internal static class CoreHost
{
    public static WebApplicationBuilder AddCoreHost(this WebApplicationBuilder builder, bool generatingCode)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        services.Configure<TenancyOptions>(configuration.GetSection("Tenancy"));
        services.Configure<BootstrapOptions>(configuration.GetSection("Bootstrap"));
        var auth = configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

        var dataPath = Path.GetFullPath(configuration["Storage:DataPath"] is { Length: > 0 } path ? path : "data");
        var connectionString = SqliteDatabase.ConnectionString(configuration, dataPath);

        services.AddPaperDotNetCore();
        services.AddScoped<ClaimsPrincipalAccessor>();
        services.AddDbContext<CoreDb>((provider, options) => options
            .UseSqlite(connectionString)
            .AddInterceptors(provider.GetRequiredService<CoreSaveChangesInterceptor>()));
        services.AddScoped<IItemQueries, SqliteItemQueries>();
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

        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, CoreJson.Default));
        services.AddProblemDetails();
        services.AddHealthChecks();
        services.AddOpenApi();

        builder.Host.UseWolverine(options =>
        {
            options.PersistMessagesWithSqlite(connectionString);
            options.UsePaperDotNetDefaults(CoreJson.Default, generatingCode);
        });
        return builder;
    }

    public static WebApplication UseCoreHost(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        TokenEndpoint.Map(app);
        Users.Map(app);
        ListEndpoints.Map(app);
        ItemEndpoints.Map(app);
        AuditEndpoints.Map(app);
        app.MapHealthChecks("/health");
        app.MapOpenApi();

        return app;
    }
}
