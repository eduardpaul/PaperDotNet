using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists;

/// <summary>
/// The lists engine: content types with typed fields, lists and libraries in workspaces, items and folders with OData
/// queries, permission scopes (ADR-0035), mutators and events. <see cref="IListItemStore"/> for other modules.
/// </summary>
public sealed class ListsModule : IModule
{
    public string Name => "Lists";

    public IJsonTypeInfoResolver Json => ListsJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ListsDbContext>();
        services.Configure<ListsOptions>(configuration.GetSection(ListsOptions.Section));
        services.AddTenantRecurringJob<RecycleBinCleanupJob>(RecycleBinCleanupJob.Name, RecycleBinCleanupJob.Schedule);
        services.AddScopes(ListScopes.All);
        services.AddMemoryCache();
        foreach (var type in FieldTypeRegistry.BuiltIn())
        {
            services.AddSingleton(type);
        }

        services.AddSingleton<FieldTypeRegistry>();
        services.AddScoped<ItemAccess>();
        services.AddScoped<IPrincipalSet>(sp => sp.GetRequiredService<ItemAccess>());
        services.AddScoped<ListSchemaLoader>();
        services.AddScoped<ItemWriter>();
        services.AddScoped(sp => new ScopeMover(sp.GetRequiredService<ListsDbContext>(), sp.GetRequiredService<IItemQueries>(), sp.GetRequiredService<IOptions<ListsOptions>>()));
        services.AddScoped<IItemQueries, SqliteItemQueries>();
        services.AddScoped<ItemQueryRunner>();
        services.AddScoped<ListItemStore>();
        services.AddScoped<IListItemStore>(sp => sp.GetRequiredService<ListItemStore>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ContentTypeEndpoints.Map(endpoints);
        ListEndpoints.Map(endpoints);
        ItemEndpoints.Map(endpoints);
        ItemCountEndpoints.Map(endpoints);
        ItemHistoryEndpoints.Map(endpoints);
        PermissionEndpoints.Map(endpoints);
        ViewEndpoints.Map(endpoints);
    }
}
