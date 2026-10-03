using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Search.Contracts;

namespace PaperDotNet.Search.Zvec;

/// <summary>
/// Makes the zvec search store available as <c>Search:Store=zvec</c> (ADR-0044). Registering it loads nothing: the
/// native library is loaded when the store is first used.
/// </summary>
public sealed class ZvecSearchModule : IModule
{
    public string Name => "SearchZvec";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ZvecOptions>(configuration.GetSection(ZvecOptions.Section));
        services.PostConfigure<ZvecOptions>(o => o.Path ??= Path.Combine(
            configuration["Storage:DataPath"] is { Length: > 0 } data ? data : "data", "search", "zvec"));
        services.AddSingleton<ZvecCollections>();
        services.AddSingleton<IBackupFolder>(sp => sp.GetRequiredService<ZvecCollections>());
        services.AddSearchStore<ZvecSearchStore>(ZvecSearchStore.StoreName);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
