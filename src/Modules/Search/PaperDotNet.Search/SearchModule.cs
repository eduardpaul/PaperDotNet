using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Mcp.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Search.Features;

namespace PaperDotNet.Search;

public static class SearchScopes
{
    public const string Read = "search.read";
    public const string Manage = "search.manage";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "Search content you can see.", GrantedToMembers: true),
        new(Manage, "Rebuild the search index."),
    ];
}

public sealed class SearchModule : IModule
{
    public string Name => "Search";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SearchDbContext>(SearchDbContext.Schema);
        services.AddScoped<ISearchIndex, SearchIndex>();
        services.AddScopes(SearchScopes.All);
        services.AddScoped<SearchReindexer>();
        services.AddScoped<IMcpTool, SearchTool>();
        services.AddOperationHandler<ReindexOperation>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => SearchEndpoints.Map(endpoints);
}
