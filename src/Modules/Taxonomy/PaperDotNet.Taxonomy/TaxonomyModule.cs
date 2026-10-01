using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Taxonomy.Data;
using PaperDotNet.Taxonomy.Features;

namespace PaperDotNet.Taxonomy;

/// <summary>
/// Taxonomy (TAX): the term store (groups → sets → hierarchical terms), keywords (folksonomy) with promotion, merges
/// and CSV import. Other modules resolve terms through <see cref="ITermStore"/>; the term store travels in templates
/// (<see cref="TermGroupTemplateHandler"/>).
/// </summary>
public sealed class TaxonomyModule : IModule
{
    public string Name => "Taxonomy";

    public IJsonTypeInfoResolver Json => TaxonomyJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TaxonomyDbContext>();
        services.AddScoped<ITermStore>(sp => new TermStore(sp.GetRequiredService<TaxonomyDbContext>()));
        services.AddScoped<ITermSetProvisioning>(sp => new TermSetProvisioner(sp.GetRequiredService<TaxonomyDbContext>(), sp.GetServices<TermSetTemplate>()));
        services.AddScoped<ITemplateHandler>(sp => new TermGroupTemplateHandler(sp.GetRequiredService<TaxonomyDbContext>()));
        services.AddScopes(TaxonomyScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        TermStoreEndpoints.Map(endpoints);
        TermSetImport.Map(endpoints);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TermGroupRequest))]
[JsonSerializable(typeof(TermGroupResponse))]
[JsonSerializable(typeof(Page<TermGroupResponse>))]
[JsonSerializable(typeof(CreateTermSetRequest))]
[JsonSerializable(typeof(UpdateTermSetRequest))]
[JsonSerializable(typeof(TermSetResponse))]
[JsonSerializable(typeof(Page<TermSetResponse>))]
[JsonSerializable(typeof(CreateTermRequest))]
[JsonSerializable(typeof(UpdateTermRequest))]
[JsonSerializable(typeof(MergeTermRequest))]
[JsonSerializable(typeof(TermResponse))]
[JsonSerializable(typeof(Page<TermResponse>))]
[JsonSerializable(typeof(List<TermResponse>))]
[JsonSerializable(typeof(KeywordRequest))]
[JsonSerializable(typeof(PromoteKeywordRequest))]
[JsonSerializable(typeof(PromoteKeywordResponse))]
[JsonSerializable(typeof(TermSetImportResponse))]
[JsonSerializable(typeof(PopularKeywordsResponse))]
[JsonSerializable(typeof(TermMerged))]
internal sealed partial class TaxonomyJson : JsonSerializerContext;
