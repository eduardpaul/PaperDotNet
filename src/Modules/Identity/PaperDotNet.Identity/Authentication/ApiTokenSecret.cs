using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace PaperDotNet.Identity.Authentication;

/// <summary>
/// Personal access token format: <c>pdn_</c> + 32 random bytes (base64url).
/// Only the SHA-256 hash is stored.
/// </summary>
internal static class ApiTokenSecret
{
    public const string Prefix = "pdn_";
    private const int DisplayPrefixLength = 12;

    public static (string Token, string DisplayPrefix, byte[] Hash) Create()
    {
        var token = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (token, token[..DisplayPrefixLength], Hash(token));
    }

    public static bool LooksLikeToken(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal) && value.Length > DisplayPrefixLength;

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
