using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Taxonomy.Data;
using PaperDotNet.Taxonomy.Features;

namespace PaperDotNet.Taxonomy;

public sealed class TaxonomyModule : IModule
{
    public string Name => "Taxonomy";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TaxonomyDbContext>(TaxonomyDbContext.Schema);
        services.AddScoped<ITermStore, TermStore>();
        services.AddScoped<ITenantInitializer, TaxonomyTenantInitializer>();
        services.AddScoped<ITemplateHandler, TermGroupTemplateHandler>();
        services.AddScopes(TaxonomyScopes.All);
        services.AddIntegrationEvent<TermMerged>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => TermStoreEndpoints.Map(endpoints);
}
