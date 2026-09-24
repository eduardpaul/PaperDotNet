using System.ComponentModel.DataAnnotations;

namespace PaperDotNet.Identity.Authentication;

/// <summary>Configuration section <c>Auth</c>.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    [Required]
    public string Issuer { get; set; } = "paperdotnet";

    [Required]
    public string Audience { get; set; } = "paperdotnet-api";

    /// <summary>HMAC key for access tokens, at least 32 characters. Set PAPERDOTNET__Auth__SigningKey.</summary>
    [Required]
    [MinLength(32, ErrorMessage = "Auth:SigningKey must be at least 32 characters.")]
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);
}

public static class AuthSchemes
{
    /// <summary>Policy scheme that forwards to JWT bearer or API token authentication.</summary>
    public const string Default = "PaperDotNet";
    public const string ApiToken = "ApiToken";
}
