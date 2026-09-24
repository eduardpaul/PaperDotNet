using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists;

public sealed class ListsModule : IModule
{
    public string Name => "Lists";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ListsDbContext>(ListsDbContext.Schema);

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
        services.AddScoped<ListSchemaLoader>();
        services.AddScoped<ItemWriter>();
        services.AddScoped<ItemQueryRunner>();
        services.AddScoped<ITenantInitializer, ListsTenantInitializer>();
        services.AddScopes(ListScopes.All);
        services.AddIntegrationEvent<ItemAdded>();
        services.AddIntegrationEvent<ItemUpdated>();
        services.AddIntegrationEvent<ItemDeleted>();
        services.AddOperationHandler<BulkUpdateOperation>();
        services.AddEventSubscriber<TermMerged, TermMergedSubscriber>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ContentTypeEndpoints.Map(endpoints);
        ListEndpoints.Map(endpoints);
        ItemEndpoints.Map(endpoints);
        BulkUpdateEndpoints.Map(endpoints);
        ViewEndpoints.Map(endpoints);
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
