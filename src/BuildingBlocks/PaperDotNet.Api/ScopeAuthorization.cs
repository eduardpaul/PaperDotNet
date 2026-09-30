using System.Collections.Frozen;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Api;

/// <summary>Requires the caller to hold <see cref="Scope"/> in their tenant.</summary>
public sealed record ScopeRequirement(string Scope) : IAuthorizationRequirement;

/// <summary>Requires the caller to be an enabled user (the default policy of every authorized endpoint).</summary>
public sealed class ActiveUserRequirement : IAuthorizationRequirement;

/// <summary>
/// Checks the user's effective scopes on every request (so role and group changes apply at once, and disabled users
/// are refused) and, for tokens limited to some scopes (API tokens, tokens requested with <c>scope</c>), the token's
/// scopes too.
/// </summary>
internal sealed class ScopeAuthorizationHandler(IServiceProvider services) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        var requirements = context.PendingRequirements.Where(r => r is ScopeRequirement or ActiveUserRequirement).ToList();
        if (requirements.Count == 0
            || context.User.FindGuid(PaperDotNetClaims.TenantId) is not { } tenantId
            || context.User.FindGuid(PaperDotNetClaims.UserId) is not { } userId
            || services.GetService<IEffectiveScopeProvider>() is not { } provider)
        {
            return;
        }

        if (await provider.GetScopesAsync(tenantId, userId, CancellationToken.None) is not { } effective)
        {
            return; // Unknown, disabled or deleted: nothing succeeds.
        }

        var limited = context.User.IsScopeLimited();
        foreach (var requirement in requirements)
        {
            if (requirement is ActiveUserRequirement
                || (requirement is ScopeRequirement { Scope: var scope } && effective.Contains(scope) && (!limited || context.User.HasScope(scope))))
            {
                context.Succeed(requirement);
            }
        }
    }
}

internal sealed class ScopeCatalog(IEnumerable<ScopeDefinition> scopes) : IScopeCatalog
{
    private readonly FrozenDictionary<string, ScopeDefinition> _scopes = scopes
        .GroupBy(s => s.Name, StringComparer.Ordinal)
        .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public IReadOnlyCollection<ScopeDefinition> All => _scopes.Values;

    public bool Contains(string scope) => _scopes.ContainsKey(scope);
}

public static class ScopeAuthorization
{
    /// <summary>Requires an enabled user who holds <paramref name="scope"/> (and a token not limited to other scopes).</summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new ActiveUserRequirement(), new ScopeRequirement(scope))
            .Build());

    /// <summary>Scope authorization; the default policy (<c>RequireAuthorization()</c>) requires an enabled user.</summary>
    public static IServiceCollection AddScopeAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options => options.DefaultPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new ActiveUserRequirement())
            .Build());
        services.AddSingleton<IScopeCatalog, ScopeCatalog>();
        services.AddScoped<IAuthorizationHandler, ScopeAuthorizationHandler>();
        return services;
    }
}
