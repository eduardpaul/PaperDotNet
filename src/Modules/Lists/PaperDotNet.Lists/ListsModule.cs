using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Lists.Templates;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists;

public sealed class ListsModule : IModule
{
    public string Name => "Lists";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ListsDbContext>(ListsDbContext.Schema);
        services.AddOptions<ListsOptions>().BindConfiguration(ListsOptions.Section);

        foreach (var type in new IFieldType[]
        {
            new TextFieldType(), new NoteFieldType(), new EmailFieldType(), new UrlFieldType(),
            new NumberFieldType(), new CurrencyFieldType(), new BooleanFieldType(),
            new DateFieldType(), new DateTimeFieldType(), new ChoiceFieldType(),
            new PersonFieldType(), new LookupFieldType(),
            new ManagedMetadataFieldType(), new KeywordsFieldType(),
        })
        {
            services.AddSingleton(type);
        }

        services.AddSingleton<FieldTypeRegistry>();
        services.TryAddScoped<IFieldTypeAvailability, AllFieldTypesAvailable>();
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
        services.AddScoped<ListSchemaLoader>();
        services.AddScoped<ItemWriter>();
        services.AddScoped<ItemQueryRunner>();
        services.AddScoped<SmartFolderQuery>();
        services.AddScoped<IListItemStore>(sp => new ListItemStore(
            sp.GetRequiredService<ListsDbContext>(), sp.GetRequiredService<ListSchemaLoader>(), sp.GetRequiredService<ItemQueryRunner>(),
            sp.GetRequiredService<ItemWriter>(), sp.GetRequiredService<IWorkspaceAccess>(), sp.GetRequiredService<ListItemSearchDocuments>()));
        services.AddScoped<ITenantInitializer, ListsTenantInitializer>();
        services.AddScoped<ListTemplateLookups>();
        services.AddScoped<ITemplateHandler, ContentTypeTemplateHandler>();
        services.AddScoped<ITemplateContainer, ListTemplateContainer>();
        services.AddScoped<ITemplateHandler, SmartFolderTemplateHandler>();
        services.AddScoped<ITemplateHandler, ListItemsTemplateHandler>();
        services.AddScopes(ListScopes.All);
        services.AddIntegrationEvent<ItemAdded>();
        services.AddIntegrationEvent<ItemUpdated>();
        services.AddIntegrationEvent<ItemDeleted>();
        services.AddIntegrationEvent<ItemRestored>();
        services.AddIntegrationEvent<ItemPurged>();
        services.AddTenantRecurringJob<RecycleBinCleanupJob>(RecycleBinCleanupJob.Name, RecycleBinCleanupJob.Schedule);
        services.AddTenantRecurringJob<ItemChangeCleanupJob>(ItemChangeCleanupJob.Name, ItemChangeCleanupJob.Schedule);
        services.AddOperationHandler<BulkUpdateOperation>();
        services.AddEventSubscriber<TermMerged, TermMergedSubscriber>();
        services.AddEventSubscriber<PrincipalDeleted, PrincipalDeletedSubscriber>();
        services.AddIntegrationEvent<ListIndexInvalidated>();
        services.AddScoped<ListItemSearchDocuments>();
        services.AddScoped<ISearchSource>(sp => sp.GetRequiredService<ListItemSearchDocuments>());
        services.AddEventSubscriber<ItemAdded, ItemSearchIndexer>();
        services.AddEventSubscriber<ItemUpdated, ItemSearchIndexer>();
        services.AddEventSubscriber<ItemRestored, ItemSearchIndexer>();
        services.AddEventSubscriber<ItemDeleted, ItemSearchIndexer>();
        services.AddEventSubscriber<ListIndexInvalidated, ItemSearchIndexer>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ContentTypeEndpoints.Map(endpoints);
        ListEndpoints.Map(endpoints);
        ItemEndpoints.Map(endpoints);
        DeltaEndpoints.Map(endpoints);
        SmartFolders.Map(endpoints);
        BulkUpdateEndpoints.Map(endpoints);
        ViewEndpoints.Map(endpoints);
        ItemHistoryEndpoints.Map(endpoints);
        PermissionEndpoints.Map(endpoints);
        ListTemplateEndpoints.Map(endpoints);
        HomeEndpoints.Map(endpoints);
    }
}

/// <summary>Creates the built-in <c>Item</c> content type (title only) for a new tenant.</summary>
internal sealed class ListsTenantInitializer(ListsDbContext db) : ITenantInitializer
{
    public async Task InitializeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        db.ContentTypes.Add(new ContentType
        {
            Id = Ids.New(),
            Name = ContentType.ItemName,
            Description = "A generic item with a title.",
            IsBuiltIn = true,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Default when no extension runtime restricts field types.</summary>
internal sealed class AllFieldTypesAvailable : IFieldTypeAvailability
{
    public ValueTask<bool> IsAvailableAsync(string fieldType, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
