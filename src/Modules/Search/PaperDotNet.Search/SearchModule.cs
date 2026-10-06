using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Mcp.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Search.Features;
using PaperDotNet.Search.Stores.Database;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Search;

public static class SearchScopes
{
    public const string Read = "search.read";
    public const string Manage = "search.manage";
    public const string Write = "search.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "Search content you can see.", GrantedToMembers: true),
        new(Manage, "Rebuild the search index."),
        new(Write, "Configure search inclusion on lists you manage.", GrantedToMembers: true),
    ];
}

public sealed class SearchModule : IModule
{
    public string Name => "Search";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SearchDbContext>(SearchDbContext.Schema);
        services.Configure<SearchOptions>(configuration.GetSection(SearchOptions.Section));

        // Stores persist prepared generations; item producers request workflows through the Lists contracts.
        services.AddSearchStore<DatabaseSearchStore>(DatabaseSearchStore.StoreName);
        services.AddSingleton<VectorIndex>();
        services.AddScoped<ISearchStore>(sp => sp.GetKeyedService<ISearchStore>(sp.GetRequiredService<IOptions<SearchOptions>>().Value.Store)
            ?? throw new InvalidOperationException($"Search:Store names no installed search store ('{sp.GetRequiredService<IOptions<SearchOptions>>().Value.Store}')."));
        services.AddScoped<ISearchIndex>(sp => sp.GetRequiredService<ISearchStore>());
        services.AddScoped<ITermUsage, TermUsage>();
        services.AddScopes(SearchScopes.All);
        services.AddScoped<SearchReindexer>();
        services.AddScoped<SearchInput>();
        services.AddScoped<SearchPolicy>();
        services.AddScoped<ITemplateHandler, SearchPolicyTemplateHandler>();
        services.AddIntegrationEvent<SearchPolicyChanged>();
        services.AddEventSubscriber<SearchPolicyChanged, SearchPolicySubscriber>();
        services.AddWorkflowTrigger(new(SearchTriggers.Requested, "An item's search data needs indexing."));
        services.AddWorkflowTrigger(new(SearchWorkflows.ReindexItem, "A rebuild requests an item's indexing and waits for it, also when automatic indexing is off.")
        { AllowDisabledBuiltIns = true, CompletionKind = SearchWorkflows.IndexRequestKind });
        services.AddWorkflowTrigger(new(SearchWorkflows.IncludeItem, "A list included again requests an item's indexing, also when automatic indexing is off.")
        { AllowDisabledBuiltIns = true });
        services.AddWorkflowTrigger(new(SearchWorkflows.RebuildRequested, "A rebuild was requested.")
        { AllowDisabledBuiltIns = true, CompletionKind = SearchWorkflows.RebuildRequestKind });
        services.AddWorkflowTrigger(new(SearchTriggers.ContainerChanged, "A library's inclusion in search or its schema changed (data: containerId)."));
        services.AddWorkflowTrigger(new(SearchTriggers.ScopesChanged, "Items moved to other permission scopes (data: itemIds)."));
        services.AddScoped<SearchStaging>();
        services.AddWorkflowActivity<SearchChunkActivity>();
        services.AddWorkflowActivity<SearchStageActivity>();
        services.AddWorkflowActivity<SearchEnrichActivity>();
        services.AddWorkflowActivity<SearchPublishActivity>();
        services.AddWorkflowActivity<SearchEmbedActivity>();
        services.AddWorkflowActivity<SearchRemoveActivity>();
        services.AddWorkflowActivity<SearchScopesActivity>();
        services.AddWorkflowActivity<SearchContainerActivity>();
        services.AddWorkflowActivity<SearchRebuildActivity>();
        services.AddWorkflow(SearchWorkflows.IndexWorkflow);
        services.AddWorkflow(SearchWorkflows.IndexEnrichedWorkflow);
        services.AddWorkflow(SearchWorkflows.RemoveWorkflow);
        services.AddWorkflow(SearchWorkflows.ContainerWorkflow);
        services.AddWorkflow(SearchWorkflows.ScopesWorkflow);
        services.AddWorkflow(SearchWorkflows.RebuildWorkflow);
        services.AddScoped<IMcpTool, SearchTool>();
        services.AddOperationHandler<ReindexOperation>();

        // Semantic and hybrid search (SRC-07, SRC-08); active when an embedding provider is configured (AI:Embeddings).
        services.AddMemoryCache();
        services.AddSingleton<EmbeddingModel>();
        services.AddSingleton<ISearchVectorSpace>(sp => sp.GetRequiredService<EmbeddingModel>());
        services.AddScoped<SearchService>();

    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        SearchEndpoints.Map(endpoints);
        SearchPolicyEndpoints.Map(endpoints);
    }
}
