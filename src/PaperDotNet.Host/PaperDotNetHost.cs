using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Audit;
using PaperDotNet.Calendar;
using PaperDotNet.Collaboration;
using PaperDotNet.ExtensionHost;
using PaperDotNet.ExtensionHost.Runtime;
using PaperDotNet.Extensions;
using PaperDotNet.Extensions.Generated;
using PaperDotNet.Identity;
using PaperDotNet.Jobs;
using PaperDotNet.Lists;
using PaperDotNet.Messaging;
using PaperDotNet.Notes;
using PaperDotNet.Notifications;
using PaperDotNet.Persistence;
using PaperDotNet.Persistence.Sqlite;
using PaperDotNet.Storage;
using PaperDotNet.Tasks;
using PaperDotNet.Taxonomy;
using PaperDotNet.Workflows;
using PaperDotNet.Workspaces;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.Sqlite;

namespace PaperDotNet.Host;

/// <summary>
/// Composes the modules into the Native AOT server (ADR-0039). Modules still to port (Documents, Tasks, Calendar,
/// Search, Taxonomy, …) stay in src/Modules out of the build until they follow the AOT rules.
/// </summary>
internal static class PaperDotNetHost
{
    private static readonly IModule[] Modules =
        [new IdentityModule(), new ListsModule(), new AuditModule(), new WorkflowsModule(), new JobsModule(), new WorkspacesModule(), new ExtensionHostModule(),
            new NotificationsModule(), new CollaborationModule(), new NotesModule(), new TasksModule(), new CalendarModule(), new TaxonomyModule()];

    /// <summary>Extensions added besides those this build references (tests register theirs here before the host starts).</summary>
    public static List<IExtension> AdditionalExtensions { get; } = [];

    public static WebApplicationBuilder AddPaperDotNet(this WebApplicationBuilder builder, bool generatingCode)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;
        var dataPath = Path.GetFullPath(configuration["Storage:DataPath"] is { Length: > 0 } path ? path : "data");
        var connectionString = services.AddSqliteDatabase(configuration, dataPath);

        services.AddPaperDotNetApi();
        services.AddPaperDotNetPersistence();
        services.AddPaperDotNetMessaging();
        services.AddPaperDotNetStorage(configuration);
        foreach (var module in Modules)
        {
            module.AddServices(services, configuration);
        }

        services.AddPaperDotNetExtensions(configuration, [.. ReferencedExtensions.Create(), .. AdditionalExtensions]);

        // Source-generated JSON of every module, for the API and the message queue.
        var json = JsonTypeInfoResolver.Combine([.. Modules.Select(m => m.Json).OfType<IJsonTypeInfoResolver>()]);
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, json));
        services.AddProblemDetails();
        services.AddHealthChecks();
        services.AddOpenApi();

        builder.Host.UseWolverine(options =>
        {
            options.PersistMessagesWithSqlite(new SqliteDataSource(connectionString, MessageStorePragmas()));
            options.UsePaperDotNetDefaults(json, generatingCode);
            foreach (var module in Modules)
            {
                options.Discovery.IncludeAssembly(module.GetType().Assembly);
            }
        });
        return builder;
    }

    /// <summary>
    /// SQLite settings of the message store's connections: Weasel's defaults with SQLite's own page cache (2 MB per
    /// connection, not 64 MB) and no memory-mapped I/O (not 256 MB of the database file mapped into the process), like
    /// the modules' connections. With Weasel's values, resident memory grew with the size of the database under load.
    /// </summary>
    private static SqlitePragmaSettings MessageStorePragmas()
    {
        var settings = SqlitePragmaSettings.Default;
        settings.CacheSize = -2000;
        settings.MmapSize = 0;
        return settings;
    }

    public static WebApplication UsePaperDotNet(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        foreach (var module in Modules)
        {
            module.MapEndpoints(app);
        }

        app.MapHealthChecks("/health");
        app.MapOpenApi();
        return app;
    }
}
