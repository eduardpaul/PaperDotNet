using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperDotNet.Lists.Contracts;

/// <summary>
/// A typed field (column) of a content type. Stored as JSON inside the content type.
/// Type-specific settings are optional properties; each field type validates the ones it uses.
/// </summary>
public sealed class FieldDefinition
{
    /// <summary>Internal name used in the API and queries (camelCase, stable).</summary>
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Field type name, e.g. <c>text</c>, <c>number</c>, <c>choice</c>, <c>lookup</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool Required { get; set; }

    /// <summary>Multiple values (choice, person, lookup).</summary>
    public bool AllowMultiple { get; set; }

    public int? MaxLength { get; set; }

    public decimal? Minimum { get; set; }

    public decimal? Maximum { get; set; }

    public List<string> Choices { get; set; } = [];

    /// <summary>Target list of a lookup field.</summary>
    public Guid? LookupListId { get; set; }

    /// <summary>Term set of a managed metadata field.</summary>
    public Guid? TermSetId { get; set; }

    /// <summary>ISO 4217 code of a currency field, e.g. <c>EUR</c>.</summary>
    public string? CurrencyCode { get; set; }

    /// <summary>Default value as JSON text, applied on create when no value is given.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>How the field counts in full-text search (SRC-06); null means <see cref="FieldSearchWeight.Normal"/>.</summary>
    public FieldSearchWeight? Search { get; set; }
}

/// <summary>Weight of a field in full-text search (SRC-06).</summary>
public enum FieldSearchWeight
{
    /// <summary>Not indexed.</summary>
    None = 0,

    /// <summary>Indexed with the body text.</summary>
    Normal = 1,

    /// <summary>Indexed with high weight (ranks close to the title).</summary>
    High = 2,
}

/// <summary>How a field's values are stored in the item JSON and compared in queries.</summary>
public enum FieldValueKind
{
    Text,
    Number,
    Boolean,

    /// <summary>Stored as <c>yyyy-MM-dd</c>; compared as text.</summary>
    Date,

    /// <summary>Stored as fixed-width UTC ISO 8601; compared as text.</summary>
    DateTime,

    /// <summary>A GUID (user or item id), stored as a lowercase string.</summary>
    Identifier,
}

/// <summary>Lookups needed while validating values (users, lookup targets, terms).</summary>
public interface IFieldValidationContext
{
    Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> ItemExistsAsync(Guid listId, Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a term id or label to an assignable term of <paramref name="termSetId"/>
    /// (null = the keywords set); new labels are added to open term sets.
    /// </summary>
    Task<Guid?> ResolveTermAsync(Guid? termSetId, string value, CancellationToken cancellationToken);
}

/// <summary>Result of normalizing one value: the value to store, or an error.</summary>
public readonly record struct FieldValueResult(JsonNode? Value, string? Error)
{
    public static FieldValueResult Ok(JsonNode? value) => new(value, null);

    public static FieldValueResult Fail(string error) => new(null, error);
}

/// <summary>
/// A field (column) type. Built-in types live in the Lists module; extensions
/// contribute more through the same interface (<c>IExtensionBuilder.AddFieldType</c>).
/// Names of extension types start with the extension id (e.g. <c>acme.iban</c>).
/// </summary>
public interface IFieldType
{
    string Name { get; }

    FieldValueKind ValueKind { get; }

    bool SupportsMultiple { get; }

    IEnumerable<string> ValidateDefinition(FieldDefinition field);

    /// <summary>Validates and normalizes a value from the API (never null: nulls are handled by the caller).</summary>
    ValueTask<FieldValueResult> NormalizeAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken);
}

/// <summary>Base for field types: handles multi-value arrays and delegates single values.</summary>
public abstract class FieldType : IFieldType
{
    public abstract string Name { get; }

    public abstract FieldValueKind ValueKind { get; }

    public virtual bool SupportsMultiple => false;

    public virtual IEnumerable<string> ValidateDefinition(FieldDefinition field)
    {
        if (field.AllowMultiple && !SupportsMultiple)
        {
            yield return $"Field type '{Name}' does not support multiple values.";
        }
    }

    public async ValueTask<FieldValueResult> NormalizeAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken)
    {
        if (!field.AllowMultiple)
        {
            return value.ValueKind == JsonValueKind.Array
                ? FieldValueResult.Fail("A single value is expected.")
                : await NormalizeSingleAsync(value, field, context, cancellationToken);
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return FieldValueResult.Fail("An array of values is expected.");
        }

        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in value.EnumerateArray())
        {
            var single = await NormalizeSingleAsync(element, field, context, cancellationToken);
            if (single.Error is not null)
            {
                return single;
            }

            if (single.Value is not null && seen.Add(single.Value.ToJsonString()))
            {
                result.Add(single.Value);
            }
        }

        return FieldValueResult.Ok(result);
    }

    protected abstract ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken);

    protected static ValueTask<FieldValueResult> Done(FieldValueResult result) => ValueTask.FromResult(result);
}

/// <summary>Canonical text forms used for storage and query constants.</summary>
public static class FieldFormats
{
    public static string DateTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Identifier(Guid value) => value.ToString("D");
}

/// <summary>
/// Whether a field type may be used in new field definitions of the current tenant
/// (e.g. types of extensions that are disabled for the tenant are not).
/// </summary>
public interface IFieldTypeAvailability
{
    ValueTask<bool> IsAvailableAsync(string fieldType, CancellationToken cancellationToken);
}
