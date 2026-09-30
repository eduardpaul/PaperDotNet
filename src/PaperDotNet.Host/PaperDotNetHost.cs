using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Audit;
using PaperDotNet.Identity;
using PaperDotNet.Jobs;
using PaperDotNet.Lists;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Persistence.Sqlite;
using PaperDotNet.Storage;
using PaperDotNet.Workflows;
using PaperDotNet.Workspaces;
using Wolverine;
using Wolverine.Sqlite;

namespace PaperDotNet.Host;

/// <summary>
/// Composes the modules into the Native AOT server (ADR-0039). Modules still to port (Documents, Tasks, Calendar,
/// Search, Taxonomy, Notifications, …) stay in src/Modules out of the build until they follow the AOT rules.
/// </summary>
internal static class PaperDotNetHost
{
    private static readonly IModule[] Modules = [new IdentityModule(), new ListsModule(), new AuditModule(), new WorkflowsModule(), new JobsModule(), new WorkspacesModule()];

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

        // Source-generated JSON of every module, for the API and the message queue.
        var json = JsonTypeInfoResolver.Combine([.. Modules.Select(m => m.Json).OfType<IJsonTypeInfoResolver>()]);
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, json));
        services.AddProblemDetails();
        services.AddHealthChecks();
        services.AddOpenApi();

        builder.Host.UseWolverine(options =>
        {
            options.PersistMessagesWithSqlite(connectionString);
            options.UsePaperDotNetDefaults(json, generatingCode);
            foreach (var module in Modules)
            {
                options.Discovery.IncludeAssembly(module.GetType().Assembly);
            }
        });
        return builder;
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
