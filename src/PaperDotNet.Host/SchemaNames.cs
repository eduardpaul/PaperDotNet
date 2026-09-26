using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;

namespace PaperDotNet.Host;

/// <summary>
/// OpenAPI schema names are type names, so two types with the same name in different modules would silently share
/// one schema and generated SDKs would get the wrong shape for one of them. This fails the document instead.
/// </summary>
internal static class SchemaNames
{
    private static readonly ConcurrentDictionary<string, Type> Owners = new(StringComparer.Ordinal);

    public static string? Unique(JsonTypeInfo type)
    {
        var name = OpenApiOptions.CreateDefaultSchemaReferenceId(type);
        if (name is null)
        {
            return null;
        }

        // T and T? share a schema.
        var actual = Nullable.GetUnderlyingType(type.Type) ?? type.Type;
        var owner = Owners.GetOrAdd(name, actual);
        return owner == actual || SameEnum(owner, actual)
            ? name
            : throw new InvalidOperationException(
                $"The OpenAPI schema name '{name}' is used by {owner.FullName} and {actual.FullName}. Rename one of the types.");
    }

    /// <summary>Enums with the same members have the same schema (e.g. a module's own copy of User/Group).</summary>
    private static bool SameEnum(Type a, Type b) =>
        a.IsEnum && b.IsEnum && Enum.GetNames(a).SequenceEqual(Enum.GetNames(b), StringComparer.Ordinal);
}
