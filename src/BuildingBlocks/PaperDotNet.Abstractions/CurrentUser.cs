namespace PaperDotNet.Abstractions;

/// <summary>The authenticated principal of the current operation.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    bool IsAuthenticated => UserId is not null;
}

/// <summary>Lets background work act as a user (e.g. the user who started an operation).</summary>
public interface ICurrentUserOverride
{
    void ActAs(Guid userId);
}

/// <summary>Claim types issued by PaperDotNet.</summary>
public static class PaperDotNetClaims
{
    public const string UserId = "sub";
    public const string TenantId = "tenant_id";
    public const string TenantIdentifier = "tenant";
    public const string Name = "name";

    /// <summary>Present only for API tokens: the scopes the token was limited to.</summary>
    public const string TokenScope = "token_scope";

    public const string TokenId = "token_id";

    /// <summary>Present on OAuth tokens granted without the <c>api</c> scope: limited to their <see cref="TokenScope"/> claims.</summary>
    public const string ScopeLimited = "scope_limited";
}

/// <summary>Authentication scheme names shared across modules.</summary>
public static class AuthenticationSchemeNames
{
    /// <summary>Personal API tokens (<c>pdn_…</c>); can authenticate before the tenant is resolved.</summary>
    public const string ApiToken = "ApiToken";
}
