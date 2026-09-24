namespace PaperDotNet.Lists.Data;

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

    /// <summary>ISO 4217 code of a currency field, e.g. <c>EUR</c>.</summary>
    public string? CurrencyCode { get; set; }

    /// <summary>Default value as JSON text, applied on create when no value is given.</summary>
    public string? DefaultValue { get; set; }
}
