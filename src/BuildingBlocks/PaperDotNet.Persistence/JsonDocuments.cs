using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PaperDotNet.Persistence;

/// <summary>
/// Query functions over JSON document columns (string properties marked with
/// <see cref="JsonDocumentExtensions.IsJsonDocument"/>). They only work inside
/// LINQ queries: each database provider translates them to SQL
/// (PostgreSQL: jsonb functions and <c>@&gt;</c>; SQLite: <c>json_extract</c> and a
/// registered containment function).
/// </summary>
public static class JsonFunctions
{
    /// <summary>Text value of a top-level property, or null.</summary>
    public static string? Text(string document, string property) => throw ClientSide();

    /// <summary>Numeric value of a top-level property, or null.</summary>
    public static double? Number(string document, string property) => throw ClientSide();

    /// <summary>Boolean value of a top-level property, or null.</summary>
    public static bool? Boolean(string document, string property) => throw ClientSide();

    /// <summary>
    /// True when <paramref name="document"/> contains <paramref name="jsonFragment"/>
    /// (PostgreSQL <c>@&gt;</c> semantics: objects by keys, arrays by elements).
    /// </summary>
    public static bool Contains(string document, string jsonFragment) => throw ClientSide();

    /// <summary>True when the top-level property exists.</summary>
    public static bool HasProperty(string document, string property) => throw ClientSide();

    private static NotSupportedException ClientSide() =>
        new("JsonFunctions can only be used in database queries.");
}

public static class JsonDocumentExtensions
{
    public const string JsonDocumentAnnotation = "PaperDotNet:JsonDocument";
    public const string ContainmentIndexAnnotation = "PaperDotNet:JsonContainmentIndex";

    /// <summary>Marks a string property as a JSON document column (PostgreSQL: <c>jsonb</c>).</summary>
    public static PropertyBuilder<string> IsJsonDocument(this PropertyBuilder<string> property) =>
        property.HasAnnotation(JsonDocumentAnnotation, true);

    /// <summary>
    /// Marks an index on a JSON document column as a containment index
    /// (PostgreSQL: GIN <c>jsonb_path_ops</c>; providers without one drop the index).
    /// </summary>
    public static IndexBuilder IsJsonContainmentIndex(this IndexBuilder index) =>
        index.HasAnnotation(ContainmentIndexAnnotation, true);
}
