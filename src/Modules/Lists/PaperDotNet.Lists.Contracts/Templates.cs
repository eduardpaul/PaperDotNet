namespace PaperDotNet.Lists.Contracts;

/// <summary>
/// A content type provided by the system or an extension, provisioned into a tenant
/// on demand and identified there by <see cref="Key"/>. Extension content types are
/// managed by the extension (tenants cannot change them).
/// </summary>
public sealed record ContentTypeTemplate(string Key, string Name, string? Description, IReadOnlyList<FieldDefinition> Fields)
{
    /// <summary>Owning extension; null for built-in content types.</summary>
    public string? ExtensionId { get; init; }
}

/// <summary>A saved view created with a list. <see cref="Layout"/>: <c>table</c>, <c>board</c>, <c>calendar</c> or <c>gallery</c>.</summary>
public sealed record ViewTemplate(
    string Name,
    IReadOnlyList<string> Columns,
    string? Filter = null,
    string? OrderBy = null,
    string? GroupBy = null,
    string Layout = "table",
    bool IsDefault = false);

/// <summary>A list template (LST-16): content types, views and settings for a new list.</summary>
public sealed record ListTemplateDefinition(
    string Key,
    string Name,
    string? Description,
    IReadOnlyList<string> ContentTypeKeys,
    IReadOnlyList<ViewTemplate> Views)
{
    /// <summary>A document library (files, from phase 3) instead of a list.</summary>
    public bool IsLibrary { get; init; }

    public bool AllowFolders { get; init; } = true;

    public bool Versioning { get; init; }

    /// <summary>Owning extension; null for built-in templates.</summary>
    public string? ExtensionId { get; init; }
}

/// <summary>Whether an extension's contributions are active in the current tenant (implemented by the extension runtime).</summary>
public interface IExtensionAvailability
{
    ValueTask<bool> IsEnabledAsync(string extensionId, CancellationToken cancellationToken);
}

/// <summary>Provisions an extension's content types into the current tenant (called when it is enabled).</summary>
public interface IContentTypeProvisioning
{
    Task ProvisionExtensionAsync(string extensionId, CancellationToken cancellationToken);
}
