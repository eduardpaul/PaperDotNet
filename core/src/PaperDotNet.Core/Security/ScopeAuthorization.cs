using Microsoft.AspNetCore.Builder;

namespace PaperDotNet.Core.Security;

public static class ScopeAuthorization
{
    /// <summary>Requires an authenticated caller whose token carries <paramref name="scope"/>.</summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.HasScope(scope)));
}
