using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists;

/// <summary>Lists and items (ADR-0039 slice of the lists engine: typed fields, OData queries, events).</summary>
public sealed class ListsModule : IModule
{
    public string Name => "Lists";

    public IJsonTypeInfoResolver Json => ListsJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ListsDbContext>();
        services.AddScopes(ListScopes.All);
        services.AddScoped<IItemQueries, SqliteItemQueries>();
        services.AddScoped<ListItemStore>();
        services.AddScoped<IListItemStore>(provider => provider.GetRequiredService<ListItemStore>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ListEndpoints.Map(endpoints);
        ItemEndpoints.Map(endpoints);
    }
}
