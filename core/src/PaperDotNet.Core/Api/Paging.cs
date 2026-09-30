using System.Buffers.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace PaperDotNet.Core.Api;

/// <summary>
/// Paging with <c>$top</c> and the opaque <c>$skiptoken</c> from the previous page's <c>@odata.nextLink</c>.
/// A token is either the last id (keyset, the default order) or an offset (explicit <c>$orderby</c>).
/// </summary>
public readonly record struct PageRequest(int Top, Guid? After, int Offset)
{
    public const int DefaultTop = 50;
    public const int MaxTop = 500;

    /// <summary>From the <c>$top</c> and <c>$skiptoken</c> options (bind them with <c>[FromQuery(Name = "$top")]</c>).</summary>
    public static PageRequest Create(int? top, string? skipToken)
    {
        Guid? after = null;
        var offset = 0;
        if (!string.IsNullOrEmpty(skipToken))
        {
            TryDecode(skipToken, out after, out offset);
        }

        return new PageRequest(Math.Clamp(top ?? DefaultTop, 1, MaxTop), after, offset);
    }

    public static string EncodeCursor(Guid id) => Base64Url.EncodeToString(id.ToByteArray());

    public static string EncodeOffset(int offset) => Base64Url.EncodeToString(BitConverter.GetBytes(offset));

    private static void TryDecode(string value, out Guid? after, out int offset)
    {
        after = null;
        offset = 0;
        Span<byte> bytes = stackalloc byte[16];
        if (!Base64Url.TryDecodeFromChars(value, bytes, out var written))
        {
            return;
        }

        if (written == 16)
        {
            after = new Guid(bytes);
        }
        else if (written == 4)
        {
            offset = Math.Max(0, BitConverter.ToInt32(bytes));
        }
    }
}

/// <summary>A page of results in Graph/OData shape.</summary>
public sealed record Page<T>(
    [property: JsonPropertyName("value")] IReadOnlyList<T> Value,
    [property: JsonPropertyName("@odata.nextLink"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextLink,
    [property: JsonPropertyName("@odata.count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Count = null);

public static class Page
{
    /// <summary>
    /// Builds a keyset page from <paramref name="items"/>, fetched with <c>Take(top + 1)</c>
    /// so we know whether another page exists.
    /// </summary>
    public static Page<T> Create<T>(IReadOnlyList<T> items, PageRequest page, HttpRequest request, Func<T, Guid> idSelector, long? count = null)
    {
        if (items.Count <= page.Top)
        {
            return new Page<T>(items, null, count);
        }

        var value = items.Take(page.Top).ToList();
        return new Page<T>(value, NextLink(request, PageRequest.EncodeCursor(idSelector(value[^1]))), count);
    }

    /// <summary>Builds an offset page (for explicit orders), fetched with <c>Take(top + 1)</c>.</summary>
    public static Page<T> CreateAtOffset<T>(IReadOnlyList<T> items, PageRequest page, HttpRequest request, long? count = null)
    {
        if (items.Count <= page.Top)
        {
            return new Page<T>(items, null, count);
        }

        var value = items.Take(page.Top).ToList();
        return new Page<T>(value, NextLink(request, PageRequest.EncodeOffset(page.Offset + page.Top)), count);
    }

    private static string NextLink(HttpRequest request, string token)
    {
        var query = request.Query
            .Where(q => q.Key is not "$skiptoken")
            .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}")
            .Append($"$skiptoken={token}");
        return $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}?{string.Join('&', query)}";
    }
}
