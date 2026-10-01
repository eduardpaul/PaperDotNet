using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Lists.Templates;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Taxonomy.Contracts;

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
        services.AddTenantRecurringJob<ItemChangeCleanupJob>(ItemChangeCleanupJob.Name, ItemChangeCleanupJob.Schedule);
        services.AddTenantRecurringJob<IndexedFieldBackfillJob>(IndexedFieldBackfillJob.Name, IndexedFieldBackfillJob.Schedule);
        services.AddScopes(ListScopes.All);
        services.AddMemoryCache();
        foreach (var type in FieldTypeRegistry.BuiltIn())
        {
            services.AddSingleton(type);
        }

        services.AddSingleton<FieldTypeRegistry>();
        services.TryAddScoped<IExtensionAvailability, NoExtensions>();
        foreach (var template in BuiltInTemplates.ContentTypes)
        {
            services.AddSingleton(template);
        }

        foreach (var template in BuiltInTemplates.Lists)
        {
            services.AddSingleton(template);
        }

        services.AddSingleton<ListTemplateRegistry>();
        services.AddScoped<ContentTypeProvisioner>();
        services.AddScoped<IContentTypeProvisioning>(sp => sp.GetRequiredService<ContentTypeProvisioner>());
        services.AddScoped<ItemAccess>();
        services.AddScoped<IPrincipalSet>(sp => sp.GetRequiredService<ItemAccess>());
        services.AddScoped<IItemAccess>(sp => sp.GetRequiredService<ItemAccess>());
        services.AddScoped(sp => new ItemSearchDocuments(
            sp.GetRequiredService<ListsDbContext>(), sp.GetRequiredService<ITermStore>(), sp.GetRequiredService<ISearchIndex>(), sp.GetServices<IItemSearchContributor>()));
        services.AddScoped<ISearchSource>(sp => sp.GetRequiredService<ItemSearchDocuments>());
        services.AddScoped<SmartFolderQuery>();
        services.AddScoped<ListSchemaLoader>();
        services.AddScoped<ItemWriter>();
        services.AddOperationHandler<BulkUpdateOperation>();
        services.AddScoped(sp => new ScopeMover(sp.GetRequiredService<ListsDbContext>(), sp.GetRequiredService<IItemQueries>(), sp.GetRequiredService<IOptions<ListsOptions>>()));
        services.AddScoped<IItemQueries, SqliteItemQueries>();
        services.AddScoped<ItemQueryRunner>();
        services.AddScoped<ListItemStore>();
        services.AddScoped<IListItemStore>(sp => sp.GetRequiredService<ListItemStore>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ContentTypeEndpoints.Map(endpoints);
        ListTemplateEndpoints.Map(endpoints);
        ListEndpoints.Map(endpoints);
        ItemEndpoints.Map(endpoints);
        ItemCountEndpoints.Map(endpoints);
        BulkUpdateEndpoints.Map(endpoints);
        DeltaEndpoints.Map(endpoints);
        ItemHistoryEndpoints.Map(endpoints);
        PermissionEndpoints.Map(endpoints);
        SmartFolders.Map(endpoints);
        ViewEndpoints.Map(endpoints);
    }
}
