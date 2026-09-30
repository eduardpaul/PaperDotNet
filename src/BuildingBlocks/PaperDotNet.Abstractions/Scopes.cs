using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.Abstractions;

/// <summary>A permission a role can grant, e.g. <c>list.write</c> (ADR-0011).</summary>
/// <param name="Name">Scope name, <c>area.action</c>.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="GrantedToMembers">Included in the built-in Member role of every new tenant.</param>
public sealed record ScopeDefinition(string Name, string Description, bool GrantedToMembers = false);

/// <summary>All scopes known to the server, contributed by modules with <c>AddScopes</c>.</summary>
public interface IScopeCatalog
{
    IReadOnlyCollection<ScopeDefinition> All { get; }

    bool Contains(string scope);
}

/// <summary>
/// Effective scopes of a user in a tenant: the scopes of the roles assigned to the user directly or through groups
/// (including groups inside groups). Null for unknown, disabled or deleted users. Implemented by the Identity module.
/// </summary>
public interface IEffectiveScopeProvider
{
    Task<IReadOnlySet<string>?> GetScopesAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}

public static class ScopeServiceCollectionExtensions
{
    /// <summary>Adds scopes to the catalog (<see cref="IScopeCatalog"/>).</summary>
    public static IServiceCollection AddScopes(this IServiceCollection services, params ScopeDefinition[] scopes)
    {
        foreach (var scope in scopes)
        {
            services.AddSingleton(scope);
        }

        return services;
    }
}

/// <summary>
/// A counter per tenant that changes whenever what users may access changes (users, groups, roles, API tokens,
/// workspace memberships): caches of access (effective scopes, principals) include it in their keys, so a change
/// applies to the next request on this server. Other servers catch up when their cache entries expire (a minute).
/// </summary>
public static class AccessGeneration
{
    private static readonly ConcurrentDictionary<Guid, long> Generations = new();

    public static long Current(Guid tenantId) => Generations.GetValueOrDefault(tenantId);

    public static void Next(Guid tenantId) => Generations.AddOrUpdate(tenantId, 1, (_, value) => value + 1);
}
