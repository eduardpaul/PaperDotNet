namespace PaperDotNet.Api;

/// <summary>
/// Enum values in the query string. Minimal APIs bind enums case-sensitively (<c>Pending</c>), while the API documents
/// and returns camelCase (<c>pending</c>), so endpoints read them as strings, parse them here and document them with
/// <see cref="OpenApiEndpointExtensions.WithQueryEnum{TEnum}"/>.
/// </summary>
public static class EnumQuery
{
    /// <summary>Parses a documented name in any case; no value is valid (null), numbers are not.</summary>
    public static bool TryParse<TEnum>(string? value, out TEnum? result)
        where TEnum : struct, Enum
    {
        result = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (!char.IsLetter(value[0]) || !Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    /// <summary>The validation message for an invalid value: the names the API accepts.</summary>
    public static string[] Invalid<TEnum>()
        where TEnum : struct, Enum =>
        [$"Use {string.Join(", ", Enum.GetNames<TEnum>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}."];
}
