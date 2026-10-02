using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PaperDotNet.Lists.Features;

/// <summary>Small, flat scalar property bags. Keys are safe top-level JSON paths and OData identifiers.</summary>
internal static partial class RelationshipAttributes
{
    public static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    public static void Validate(JsonObject? attributes)
    {
        if (attributes is null) return;
        if (attributes.Count > 64 || Encoding.UTF8.GetByteCount(attributes.ToJsonString()) > 16384)
            throw new ArgumentException("Relationship attributes allow at most 64 keys and 16 KiB of JSON.");
        foreach (var (key, value) in attributes)
        {
            if (key.Length > 128 || !KeyPattern().IsMatch(key))
                throw new ArgumentException("Attribute keys must start with a letter or underscore and contain only letters, digits, underscores (maximum 128 characters).");
            if (value is not null && value.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw new ArgumentException("Relationship attributes must be strings, numbers or booleans; null removes a key.");
            if (value?.GetValueKind() == JsonValueKind.Number && (!double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)))
                throw new ArgumentException("Attribute numbers must be finite.");
        }
    }

    public static JsonObject Initial(JsonObject? attributes) => Patch(new JsonObject(), attributes ?? []);

    public static JsonObject Patch(JsonObject current, JsonObject patch)
    {
        Validate(patch);
        var result = current.DeepClone().AsObject();
        foreach (var (key, value) in patch)
        {
            if (value is null || value.GetValueKind() == JsonValueKind.Null) result.Remove(key);
            else result[key] = value.DeepClone();
        }
        Validate(result);
        return result;
    }

    public static JsonObject? Merge(JsonObject target, JsonObject source)
    {
        var result = target.DeepClone().AsObject();
        foreach (var (key, value) in source)
        {
            if (result.TryGetPropertyValue(key, out var existing) && !JsonNode.DeepEquals(existing, value)) return null;
            result[key] = value?.DeepClone();
        }
        Validate(result);
        return result;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
