using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.Abstractions;

/// <summary>A permission a role can grant, e.g. <c>workspace.create</c>.</summary>
/// <param name="Name">Scope name, <c>area.action</c>.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="GrantedToMembers">Included in the built-in Member role of every tenant.</param>
public sealed record ScopeDefinition(string Name, string Description, bool GrantedToMembers = false);

/// <summary>All scopes known to the system, contributed by modules (and later extensions).</summary>
public interface IScopeCatalog
{
    IReadOnlyCollection<ScopeDefinition> All { get; }

    bool Contains(string scope);
}

/// <summary>
/// Effective scopes of a user in the current tenant (roles assigned directly
/// or through groups). Implemented by the Identity module.
/// </summary>
public interface IEffectiveScopeProvider
{
    Task<IReadOnlySet<string>?> GetScopesAsync(Guid userId, CancellationToken cancellationToken);
}

public static class ScopeServiceCollectionExtensions
{
    public static IServiceCollection AddScopes(this IServiceCollection services, params ScopeDefinition[] scopes)
    {
        foreach (var scope in scopes)
        {
            services.AddSingleton(scope);
        }

        return services;
    }
}
