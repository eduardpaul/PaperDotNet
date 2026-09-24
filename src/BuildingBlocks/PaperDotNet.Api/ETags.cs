using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace PaperDotNet.Api;

/// <summary>Optimistic concurrency via ETag / If-Match, based on the row version.</summary>
public static class ETags
{
    public static string From(uint version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static void Set(HttpResponse response, uint version) =>
        response.Headers[HeaderNames.ETag] = From(version);

    /// <summary>Reads the version from <c>If-Match</c>. Returns false when the header is missing or malformed.</summary>
    public static bool TryGetIfMatch(HttpRequest request, out uint version)
    {
        version = 0;
        var value = request.Headers[HeaderNames.IfMatch].ToString().Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.Length > 2
            && value[0] == '"'
            && value[^1] == '"'
            && uint.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
