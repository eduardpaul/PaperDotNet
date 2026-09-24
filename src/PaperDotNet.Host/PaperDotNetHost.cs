using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Host.Bootstrap;
using PaperDotNet.Identity;
using PaperDotNet.Persistence;
using PaperDotNet.Persistence.PostgreSql;
using PaperDotNet.ServiceDefaults;
using PaperDotNet.Tenancy;
using PaperDotNet.Workspaces;

namespace PaperDotNet.Host;

/// <summary>Composition root: modules, persistence, security and the HTTP pipeline.</summary>
public static class PaperDotNetHost
{
    public const string EnvironmentPrefix = "PAPERDOTNET__";

    /// <summary>Built-in modules, in dependency order.</summary>
    public static IReadOnlyList<IModule> Modules { get; } =
    [
        new TenancyModule(),
        new IdentityModule(),
        new WorkspacesModule(),
    ];

    public static WebApplicationBuilder AddPaperDotNet(this WebApplicationBuilder builder, bool runBootstrap)
    {
        builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);
        builder.AddServiceDefaults();

        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddHybridCache();
        services.AddPaperDotNetPostgreSql(builder.Configuration);
        services.AddSingleton<DatabaseMigrator>();
        services.AddScopeAuthorization();

        foreach (var module in Modules)
        {
            module.AddServices(services, builder.Configuration);
        }

        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        services.AddOpenApi("v1", o => o.AddDocumentTransformer<BearerSecurityTransformer>());
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    $"{ctx.Request.Host.Host}|{ctx.User.FindFirst(PaperDotNetClaims.UserId)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString()}",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1) }));
        });
        // Behind a reverse proxy, set ForwardedHeaders:Enabled=true. Off by default: forwarded
        // headers influence scheme and host (and therefore host-based tenant resolution).
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        services.AddOptions<BootstrapOptions>().BindConfiguration(BootstrapOptions.Section);
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.Section);
        services.AddScoped<TenantBootstrapper>();
        if (runBootstrap)
        {
            services.AddHostedService<StartupBootstrapService>();
        }

        return builder;
    }

    public static WebApplication UsePaperDotNet(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
        {
            app.UseForwardedHeaders();
        }
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.UsePaperDotNetTenantResolution();
        app.UseAuthentication();
        app.UsePaperDotNetTenantGuard();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapDefaultEndpoints();
        app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
        app.MapGet("/version", () => TypedResults.Ok(new { version = Version })).AllowAnonymous().ExcludeFromDescription();
        foreach (var module in Modules)
        {
            module.MapEndpoints(app);
        }

        return app;
    }

    public static string Version { get; } =
        typeof(PaperDotNetHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
