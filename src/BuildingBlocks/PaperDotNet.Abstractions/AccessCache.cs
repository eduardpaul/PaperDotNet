namespace PaperDotNet.Abstractions;

/// <summary>Cache tags for what access checks cache about a user (scopes, principals).</summary>
public static class AccessCacheTags
{
    /// <summary>Evicted when users, groups, roles or workspace memberships of the tenant change.</summary>
    public static string Principals(Guid tenantId) => $"access:principals:{tenantId}";
}
