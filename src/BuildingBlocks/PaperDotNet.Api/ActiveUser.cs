using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Api;

/// <summary>Part of the default policy: the caller must be an existing, enabled user of the tenant.</summary>
public sealed class ActiveUserRequirement : IAuthorizationRequirement;

public sealed class ActiveUserAuthorizationHandler(IEffectiveScopeProvider scopes) : AuthorizationHandler<ActiveUserRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveUserRequirement requirement)
    {
        if (Guid.TryParse(context.User.FindFirstValue(PaperDotNetClaims.UserId), out var userId)
            && await scopes.GetScopesAsync(userId, CancellationToken.None) is not null)
        {
            context.Succeed(requirement);
        }
    }
}
