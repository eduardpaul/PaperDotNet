using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Api;

/// <summary>Requires the caller to hold <see cref="Scope"/> in the current tenant.</summary>
public sealed record ScopeRequirement(string Scope) : IAuthorizationRequirement;

/// <summary>
/// Checks the user's current effective scopes (so role changes apply
/// immediately) and, for API tokens and limited OAuth tokens, the scopes the token was granted.
/// </summary>
public sealed class ScopeAuthorizationHandler(IEffectiveScopeProvider scopes) : AuthorizationHandler<ScopeRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        var sub = context.User.FindFirstValue(PaperDotNetClaims.UserId);
        if (!Guid.TryParse(sub, out var userId))
        {
            return;
        }

        var tokenScopes = context.User.FindAll(PaperDotNetClaims.TokenScope).Select(c => c.Value).ToList();
        var limited = context.User.HasClaim(c => c.Type is PaperDotNetClaims.TokenId or PaperDotNetClaims.ScopeLimited);
        if (limited && !tokenScopes.Contains(requirement.Scope))
        {
            return;
        }

        var effective = await scopes.GetScopesAsync(userId, CancellationToken.None);
        if (effective is not null && effective.Contains(requirement.Scope))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Creates a <c>scope:{name}</c> policy on demand for any scope, including extension scopes.</summary>
public sealed class ScopePolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "scope:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ScopeRequirement(policyName[Prefix.Length..]))
                .Build();
        }

        return await base.GetPolicyAsync(policyName);
    }
}

public static class ScopeAuthorizationExtensions
{
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(ScopePolicyProvider.Prefix + scope);

    /// <summary>
    /// Registers scope-based authorization. The default policy requires an
    /// authenticated, active user of the current tenant.
    /// </summary>
    public static IServiceCollection AddScopeAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ActiveUserRequirement())
                .Build();
        });
        services.AddSingleton<IAuthorizationPolicyProvider, ScopePolicyProvider>();
        services.AddSingleton<IScopeCatalog, ScopeCatalog>();
        services.AddScoped<IAuthorizationHandler, ScopeAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, ActiveUserAuthorizationHandler>();
        return services;
    }
}
