using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PaperDotNet.Persistence;

/// <summary>
/// Configures a module DbContext for the chosen database provider. The only
/// implementation today is PostgreSQL (PaperDotNet.Persistence.PostgreSql).
/// </summary>
public interface IDatabaseProvider
{
    void Configure(DbContextOptionsBuilder options, string schema);
}

/// <summary>The module DbContexts registered in this host, in registration order.</summary>
public sealed class ModuleDbContextRegistry
{
    private readonly List<Type> _contexts = [];

    public IReadOnlyList<Type> Contexts => _contexts;

    internal void Add(Type type)
    {
        if (!_contexts.Contains(type))
        {
            _contexts.Add(type);
        }
    }
}

public static class DatabaseRegistration
{
    /// <summary>
    /// Registers a module DbContext in its own schema, configured by the
    /// provider and with the PaperDotNet interceptors.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext, ITenantScopedDbContext
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<AuditingInterceptor>();
        var registry = GetRegistry(services);
        registry.Add(typeof(TContext));

        services.AddDbContext<TContext>((sp, options) =>
        {
            sp.GetRequiredService<IDatabaseProvider>().Configure(options, schema);
            options.AddInterceptors(sp.GetRequiredService<AuditingInterceptor>());
        });

        return services;
    }

    private static ModuleDbContextRegistry GetRegistry(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ModuleDbContextRegistry))?.ImplementationInstance;
        if (existing is ModuleDbContextRegistry registry)
        {
            return registry;
        }

        registry = new ModuleDbContextRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}

/// <summary>Applies pending migrations of every module DbContext.</summary>
public sealed class DatabaseMigrator(IServiceProvider services, ModuleDbContextRegistry registry)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        foreach (var type in registry.Contexts)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(type);
            await context.Database.MigrateAsync(cancellationToken);
        }
    }
}
