using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence;

/// <summary>
/// The database of the build (ADR-0039: EF Core precompiles SQL for one provider, so a binary has one). Registered by
/// the host from the provider building block (PaperDotNet.Persistence.Sqlite).
/// </summary>
public interface IDatabaseProvider
{
    void Configure(DbContextOptionsBuilder options);
}

/// <summary>
/// SQL scripts of tables outside the modules (an extension's, EXT-07), embedded in <paramref name="Assembly"/> as
/// <c>Schema.{Provider}.{migration id}.sql</c> (e.g. <c>Schema.Sqlite.20261001120000_Initial.sql</c>). The host applies
/// them after the modules' scripts, in order of their migration ids.
/// </summary>
public sealed record SchemaScripts(string Owner, Assembly Assembly);

public static class DatabaseRegistration
{
    /// <summary>The save guard (tenant isolation, versions, stamps) and the clock.</summary>
    public static IServiceCollection AddPaperDotNetPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<AuditOverrides>();
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
