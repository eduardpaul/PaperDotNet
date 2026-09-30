using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PaperDotNet.Persistence;

/// <summary>
/// The database of the build (ADR-0039: EF Core precompiles SQL for one provider, so a binary has one). Registered by
/// the host from the provider building block (PaperDotNet.Persistence.Sqlite).
/// </summary>
public interface IDatabaseProvider
{
    void Configure(DbContextOptionsBuilder options);
}

public static class DatabaseRegistration
{
    /// <summary>The save guard (tenant isolation, versions, stamps) and the clock.</summary>
    public static IServiceCollection AddPaperDotNetPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<SaveChangesGuard>();
        return services;
    }

    /// <summary>Registers a module's DbContext on the build's database, with the <see cref="SaveChangesGuard"/>.</summary>
    public static IServiceCollection AddModuleDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] TContext>(this IServiceCollection services)
        where TContext : DbContext =>
        services.AddDbContext<TContext>((provider, options) =>
        {
            provider.GetRequiredService<IDatabaseProvider>().Configure(options);
            options.AddInterceptors(provider.GetRequiredService<SaveChangesGuard>());
        });
}
