namespace PaperDotNet.Identity.Authentication;

/// <summary>Configuration section <c>Auth</c>.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Reject OAuth requests over plain HTTP. Turn off only for local development or
    /// when TLS ends at a reverse proxy that does not forward the scheme.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Allow the password grant for the first-party client <c>paperdotnet</c> (CLI,
    /// scripts). Third-party clients never get it.
    /// </summary>
    public bool AllowPasswordGrant { get; set; } = true;

    /// <summary>
    /// Sign-in with a password or a passkey stored in PaperDotNet (<c>/v1.0/auth/login</c>, passkey sign-in and the
    /// password grant). Turn it off when an authenticating reverse proxy signs everyone in, so its rules (two-factor,
    /// lockout, removed users) cannot be bypassed with a local password (ADR-0045). Requires <see cref="ReverseProxy"/>.
    /// API tokens and client credentials keep working.
    /// </summary>
    public bool LocalSignIn { get; set; } = true;

    /// <summary>
    /// Page of the (future) web UI that signs users in; <c>/connect/authorize</c>
    /// redirects there with <c>returnUrl</c> when there is no session. Without it, 401.
    /// </summary>
    public string? LoginUrl { get; set; }

    /// <summary>Redirect URIs of the first-party client (e.g. the web UI's callback).</summary>
    public List<string> FirstPartyRedirectUris { get; set; } = [];

    /// <summary>Relying-party id for passkeys (the UI's domain). Defaults to the request host.</summary>
    public string? PasskeyServerDomain { get; set; }

    /// <summary>Origins allowed to use passkeys (e.g. <c>https://docs.example.com</c>). Defaults to the request origin.</summary>
    public List<string> PasskeyOrigins { get; set; } = [];

    /// <summary>Sign-in through an authenticating reverse proxy (IAM-15), off by default.</summary>
    public ReverseProxyAuthOptions ReverseProxy { get; set; } = new();
}

/// <summary>
/// Configuration section <c>Auth:ReverseProxy</c>: an authenticating proxy (Authelia, Authentik, oauth2-proxy, Nginx
/// Proxy Manager) names the signed-in user in request headers. They are trusted only from <see cref="TrustedProxies"/>,
/// with the <see cref="Secret"/> when one is set, and only at <c>/auth/proxy/sign-in</c> and <c>/connect/authorize</c>,
/// which start a sign-in session; the API itself keeps using tokens (ADR-0031, ADR-0045).
/// </summary>
public sealed class ReverseProxyAuthOptions
{
    /// <summary>Shortest accepted <see cref="Secret"/>.</summary>
    public const int MinSecretLength = 16;

    public bool Enabled { get; set; }

    /// <summary>
    /// A secret the proxy sends in <see cref="SecretHeader"/> only where it authenticated the user. When set, headers
    /// without it are ignored, so a request that reaches the sign-in some other way cannot name a user.
    /// </summary>
    public string? Secret { get; set; }

    public string SecretHeader { get; set; } = "X-PaperDotNet-Proxy";

    /// <summary>Addresses or networks (CIDR) of the proxies whose headers are trusted, e.g. <c>172.18.0.0/16</c>. Required when enabled.</summary>
    public List<string> TrustedProxies { get; set; } = [];

    public string UserHeader { get; set; } = "Remote-User";

    public string? EmailHeader { get; set; } = "Remote-Email";

    public string? NameHeader { get; set; } = "Remote-Name";

    /// <summary>Comma-separated group names; users are added to existing groups of those names (never removed).</summary>
    public string? GroupsHeader { get; set; } = "Remote-Groups";

    /// <summary>Create unknown users (without a password) on their first sign-in.</summary>
    public bool CreateUsers { get; set; } = true;

    /// <summary><see cref="ProxyGroupSync.Add"/> only adds members; <see cref="ProxyGroupSync.Sync"/> also removes them from proxy groups the proxy no longer names.</summary>
    public ProxyGroupSync GroupSync { get; set; } = ProxyGroupSync.Add;

    /// <summary>Create groups the proxy names that do not exist yet (as proxy groups).</summary>
    public bool CreateGroups { get; set; }

    /// <summary>
    /// How long a proxy sign-in lasts before the web UI goes through the proxy again (refresh tokens and the sign-in
    /// session end then), so removed users and group changes take effect.
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Where signing out sends the browser after a proxy sign-in, e.g. the proxy's own logout page.</summary>
    public string? LogoutUrl { get; set; }
}

/// <summary>How the groups the proxy names change memberships.</summary>
public enum ProxyGroupSync
{
    /// <summary>Users join existing groups the proxy names; they are never removed.</summary>
    Add,

    /// <summary>Users also leave proxy groups (created by the proxy) that it no longer names; local groups are never changed.</summary>
    Sync,
}

public static class AuthSchemes
{
    /// <summary>Policy scheme that forwards to OAuth token validation or API token authentication.</summary>
    public const string Default = "PaperDotNet";
    public const string ApiToken = PaperDotNet.Abstractions.AuthenticationSchemeNames.ApiToken;

    /// <summary>Interactive sign-in session (cookie), used only by <c>/connect/authorize</c>.</summary>
    public const string Session = "PaperDotNet.Session";
}

/// <summary>OAuth scopes besides the permission scopes.</summary>
public static class OAuthScopes
{
    /// <summary>Full access as the user (first-party apps). Without it, tokens are limited to the permission scopes they were granted.</summary>
    public const string Api = "api";
}
