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
