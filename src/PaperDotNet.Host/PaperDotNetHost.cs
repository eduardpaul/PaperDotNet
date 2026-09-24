using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Audit;
using PaperDotNet.Calendar;
using PaperDotNet.Documents;
using PaperDotNet.ExtensionHost;
using PaperDotNet.ExtensionHost.Runtime;
using PaperDotNet.Extensions;
using PaperDotNet.Extensions.Generated;
using PaperDotNet.Host.Bootstrap;
using PaperDotNet.Identity;
using PaperDotNet.Jobs;
using PaperDotNet.Lists;
using PaperDotNet.Messaging;
using PaperDotNet.Notifications;
using PaperDotNet.Persistence;
using PaperDotNet.Persistence.PostgreSql;
using PaperDotNet.Persistence.Sqlite;
using PaperDotNet.Search;
using PaperDotNet.ServiceDefaults;
using PaperDotNet.Storage;
using PaperDotNet.Tasks;
using PaperDotNet.Taxonomy;
using PaperDotNet.Tenancy;
using PaperDotNet.Workspaces;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Sqlite;

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
        new TaxonomyModule(),
        new ListsModule(),
        new JobsModule(),
        new SearchModule(),
        new DocumentsModule(),
        new TasksModule(),
        new CalendarModule(),
        new NotificationsModule(),
        new AuditModule(),
        new ExtensionHostModule(),
    ];

    /// <summary>
    /// Extensions registered in addition to the ones referenced by this build (found by the
    /// source generator). For tests and custom hosts; set before the host is built.
    /// </summary>
    public static List<IExtension> AdditionalExtensions { get; } = [];

    /// <summary>All extensions of this host: referenced at build time plus <see cref="AdditionalExtensions"/>.</summary>
    public static IReadOnlyList<IExtension> Extensions() => [.. ReferencedExtensions.Create(), .. AdditionalExtensions];

    public static WebApplicationBuilder AddPaperDotNet(this WebApplicationBuilder builder, bool runBootstrap)
    {
        builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);
        builder.AddServiceDefaults();

        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<HttpCurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<HttpCurrentUser>());
        services.AddScoped<ICurrentUserOverride>(sp => sp.GetRequiredService<HttpCurrentUser>());
        services.AddHybridCache();
        services.AddPaperDotNetDatabase(builder.Configuration);
        services.AddPaperDotNetStorage(builder.Configuration);
        services.AddSingleton<ILiveEvents, LiveEventHub>();
        services.AddSingleton<DatabaseMigrator>();
        services.AddScoped<PaperDotNet.Host.Backup.BackupService>();
        services.AddScopeAuthorization();

        // Registered first: hosted services start in order, so migrations run before
        // Wolverine and the job scheduler touch the database.
        services.AddOptions<BootstrapOptions>().BindConfiguration(BootstrapOptions.Section);
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.Section);
        services.AddScoped<TenantBootstrapper>();
        if (runBootstrap)
        {
            services.AddHostedService<StartupBootstrapService>();
        }

        foreach (var module in Modules)
        {
            module.AddServices(services, builder.Configuration);
        }

        services.AddPaperDotNetExtensions(builder.Configuration, Extensions());

        services.AddPaperDotNetMessaging(
            options => ConfigureMessageStorage(options, builder.Configuration),
            Modules.Select(m => m.GetType().Assembly).Distinct());

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

        app.MapPaperDotNetExtensions();

        return app;
    }

    /// <summary>Registers the configured database provider (<c>Database:Provider</c>): SQLite by default, or PostgreSQL.</summary>
    public static IServiceCollection AddPaperDotNetDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration[$"{DatabaseOptions.Section}:Provider"] is { Length: > 0 } name ? name : "Sqlite";
        return provider.ToUpperInvariant() switch
        {
            "SQLITE" => services.AddPaperDotNetSqlite(configuration),
            "POSTGRESQL" or "POSTGRES" => services.AddPaperDotNetPostgreSql(configuration),
            _ => throw new InvalidOperationException($"Unknown Database:Provider '{provider}'. Use Sqlite or PostgreSql."),
        };
    }

    /// <summary>Wolverine message storage in the same database as the data (no broker needed).</summary>
    private static void ConfigureMessageStorage(WolverineOptions options, IConfiguration configuration)
    {
        if (IsPostgreSql(configuration))
        {
            options.PersistMessagesWithPostgresql(configuration.GetConnectionString(PostgreSqlServiceCollectionExtensions.ConnectionStringName)!, "wolverine");
        }
        else
        {
            options.PersistMessagesWithSqlite(Persistence.Sqlite.SqliteServiceCollectionExtensions.ResolveConnectionString(configuration));

            // SQLite serves a single app instance.
            options.Durability.Mode = DurabilityMode.Solo;
        }
    }

    private static bool IsPostgreSql(IConfiguration configuration) =>
        configuration[$"{DatabaseOptions.Section}:Provider"]?.ToUpperInvariant() is "POSTGRESQL" or "POSTGRES";

    public static string Version { get; } =
        typeof(PaperDotNetHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
