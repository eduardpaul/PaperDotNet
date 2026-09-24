using System.Buffers.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace PaperDotNet.Api;

/// <summary>
/// Keyset paging. Clients pass <c>$top</c> and the opaque <c>$skiptoken</c>
/// from the previous page's <c>@odata.nextLink</c>.
/// </summary>
public readonly record struct PageRequest(int Top, Guid? After)
{
    public const int DefaultTop = 50;
    public const int MaxTop = 500;

    public static PageRequest From(HttpRequest request)
    {
        var top = DefaultTop;
        if (request.Query.TryGetValue("$top", out var topValue) && int.TryParse(topValue, out var parsed))
        {
            top = Math.Clamp(parsed, 1, MaxTop);
        }

        Guid? after = null;
        if (request.Query.TryGetValue("$skiptoken", out var token) && TryDecodeCursor(token.ToString(), out var id))
        {
            after = id;
        }

        return new PageRequest(top, after);
    }

    public static string EncodeCursor(Guid id) => Base64Url.EncodeToString(id.ToByteArray());

    public static bool TryDecodeCursor(string value, out Guid id)
    {
        id = default;
        Span<byte> bytes = stackalloc byte[16];
        if (!Base64Url.TryDecodeFromChars(value, bytes, out var written) || written != 16)
        {
            return false;
        }

        id = new Guid(bytes);
        return true;
    }
}

/// <summary>A page of results in Graph/OData shape.</summary>
public sealed record Page<T>(
    [property: JsonPropertyName("value")] IReadOnlyList<T> Value,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

public static class Page
{
    /// <summary>
    /// Builds a page from <paramref name="items"/>, fetched with <c>Take(top + 1)</c>
    /// so we know whether another page exists.
    /// </summary>
    public static Page<T> Create<T>(IReadOnlyList<T> items, PageRequest page, HttpRequest request, Func<T, Guid> idSelector)
    {
        if (items.Count <= page.Top)
        {
            return new Page<T>(items, null);
        }

        var value = items.Take(page.Top).ToList();
        var cursor = PageRequest.EncodeCursor(idSelector(value[^1]));
        var query = request.Query
            .Where(q => q.Key is not "$skiptoken")
            .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}")
            .Append($"$skiptoken={cursor}");
        var nextLink = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}?{string.Join('&', query)}";
        return new Page<T>(value, nextLink);
    }
}
