using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PaperDotNet.Persistence;

/// <summary>
/// Configures a module DbContext for the chosen database provider:
/// SQLite (default, PaperDotNet.Persistence.Sqlite) or PostgreSQL
/// (PaperDotNet.Persistence.PostgreSql). Selected with <c>Database:Provider</c>.
/// </summary>
public interface IDatabaseProvider
{
    /// <summary><c>Sqlite</c> or <c>PostgreSql</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Configures <paramref name="options"/> for a context in <paramref name="schema"/>. Migrations come from
    /// <paramref name="migrationsAssembly"/> (null: the host's <c>PaperDotNet.Migrations.{Name}</c>).
    /// </summary>
    void Configure(DbContextOptionsBuilder options, string schema, string? migrationsAssembly = null);

    /// <summary>Runs after a module's migrations (e.g. PostgreSQL row-level security policies).</summary>
    Task AfterMigrateAsync(DbContext context, CancellationToken cancellationToken) => Task.CompletedTask;
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
    /// provider and with the PaperDotNet interceptors. Migrations live in
    /// <c>{migrationsAssemblyPrefix}.{provider}</c> (default: the host's <c>PaperDotNet.Migrations.*</c>).
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema, string? migrationsAssemblyPrefix = null)
        where TContext : DbContext, ITenantScopedDbContext
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<AuditingInterceptor>();
        var registry = GetRegistry(services);
        registry.Add(typeof(TContext));

        services.AddDbContext<TContext>((sp, options) =>
        {
            var provider = sp.GetRequiredService<IDatabaseProvider>();
            provider.Configure(options, schema, migrationsAssemblyPrefix is null ? null : $"{migrationsAssemblyPrefix}.{provider.Name}");
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
public sealed class DatabaseMigrator(IServiceProvider services, ModuleDbContextRegistry registry, IDatabaseProvider provider)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        foreach (var type in registry.Contexts)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(type);
            try
            {
                _ = context.GetService<IMigrationsAssembly>().Assembly;
            }
            catch (FileNotFoundException ex)
            {
                throw new InvalidOperationException(
                    $"The migrations of {type.Name} ({ex.FileName}) are missing: reference the migrations assembly for the {provider.Name} provider in the host.", ex);
            }

            await context.Database.MigrateAsync(cancellationToken);
            await provider.AfterMigrateAsync(context, cancellationToken);
        }
    }
}
