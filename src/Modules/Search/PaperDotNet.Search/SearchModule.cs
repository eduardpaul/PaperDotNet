using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Search.Features;
using PaperDotNet.Taxonomy.Contracts;

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

/// <summary>
/// Search (SRC): one index of documents from every module (list items today), full text with SQLite FTS5, trimmed by
/// permission scope (ADR-0035). Semantic and hybrid search (SRC-07, SRC-08) come later.
/// </summary>
public sealed class SearchModule : IModule
{
    public string Name => "Search";

    public IJsonTypeInfoResolver Json => SearchJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SearchDbContext>();
        services.AddScoped<ISearchQueries>(sp => new SqliteSearchQueries(sp.GetRequiredService<SearchDbContext>()));
        services.AddScoped<ISearchIndex>(sp => new SearchIndex(sp.GetRequiredService<SearchDbContext>(), sp.GetRequiredService<ISearchQueries>()));
        services.AddScoped<ITermUsage>(sp => new TermUsage(sp.GetRequiredService<ISearchQueries>()));
        services.AddScoped(sp => new SearchService(sp.GetRequiredService<ISearchQueries>(), sp.GetRequiredService<ITermStore>(), sp.GetRequiredService<IItemAccess>()));
        services.AddScoped<SearchReindexer>();
        services.AddScoped<PaperDotNet.Mcp.Contracts.IMcpTool>(sp => new SearchTool(sp.GetRequiredService<SearchService>(), sp.GetRequiredService<PaperDotNet.Api.Caller>()));
        services.AddOperationHandler<ReindexOperation>();
        services.AddScopes(SearchScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => SearchEndpoints.Map(endpoints);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(ReindexResponse))]
[JsonSerializable(typeof(ReindexPayload))]
[JsonSerializable(typeof(ReindexResult))]
internal sealed partial class SearchJson : JsonSerializerContext;
