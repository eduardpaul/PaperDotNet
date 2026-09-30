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
