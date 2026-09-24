using OpenIddict.EntityFrameworkCore.Models;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Identity.Data;

/// <summary>An OAuth client registered in a tenant (OpenIddict application, tenant-filtered).</summary>
public sealed class OAuthApplication : OpenIddictEntityFrameworkCoreApplication<Guid, OAuthAuthorization, OAuthToken>, ITenantOwned
{
    public const string FirstPartyClientId = "paperdotnet";

    public Guid TenantId { get; set; }

    /// <summary>The service account the client acts as with the client credentials flow.</summary>
    public Guid? ServiceUserId { get; set; }
}

[NotAudited]
public sealed class OAuthAuthorization : OpenIddictEntityFrameworkCoreAuthorization<Guid, OAuthApplication, OAuthToken>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

/// <summary>Unused store (scopes are registered in the server options), required by OpenIddict.</summary>
public sealed class OAuthScope : OpenIddictEntityFrameworkCoreScope<Guid>;

[NotAudited]
public sealed class OAuthToken : OpenIddictEntityFrameworkCoreToken<Guid, OAuthApplication, OAuthAuthorization>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

/// <summary>
/// RSA key of the OAuth server (signing identity tokens, encryption), shared by all
/// nodes. The private key is protected with ASP.NET Data Protection.
/// </summary>
public sealed class ServerKey
{
    public Guid Id { get; set; }

    /// <summary><c>sig</c> or <c>enc</c>.</summary>
    public required string Use { get; set; }

    public required string ProtectedKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
