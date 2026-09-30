using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaperDotNet.Core.Messaging;
using PaperDotNet.Core.Persistence;
using PaperDotNet.Core.Security;

namespace PaperDotNet.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>The caller, the save interceptor and the outbox.</summary>
    public static IServiceCollection AddPaperDotNetCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<CoreSaveChangesInterceptor>();
        services.AddScoped<IOutbox, WolverineOutbox>();
        return services;
    }
}
