namespace PaperDotNet.Abstractions;

/// <summary>The authenticated principal of the current operation.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    bool IsAuthenticated => UserId is not null;
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
}
