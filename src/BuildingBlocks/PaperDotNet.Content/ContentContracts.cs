using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperDotNet.Content;

/// <summary>The caller supplies authorization; persistence never implies permission from write access.</summary>
public interface IContentAuthorization
{
    Task AuthorizeAsync(string actor, ResourcesRow resource, string action, CancellationToken cancellationToken);
}

public enum ArchiveFilter { Active, Archived, All }

public sealed record ContentInput(string Name, string ContentTypeId, JsonElement Values,
    IReadOnlyList<string>? Keywords = null, string? BlobId = null);

public sealed record ContentView(ResourcesRow Resource, ItemRevisionsRow Revision, SchemaRevisionsRow Schema);

public sealed record EffectiveSchema(
    [property: JsonPropertyName("fields")] IReadOnlyList<SchemaField> Fields,
    [property: JsonPropertyName("content_types")] IReadOnlyList<SchemaContentType> ContentTypes,
    [property: JsonPropertyName("rules")] IReadOnlyList<SchemaRule>? Rules = null);

public sealed record SchemaField(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required = false,
    [property: JsonPropertyName("choices")] IReadOnlyList<string>? Choices = null,
    [property: JsonPropertyName("indexed")] bool Indexed = false,
    [property: JsonPropertyName("scale")] int Scale = 0,
    [property: JsonPropertyName("definition_id")] string? DefinitionId = null,
    [property: JsonPropertyName("options")] SchemaFieldOptions? Options = null);

public sealed record SchemaFieldOptions(
    [property: JsonPropertyName("unique")] bool Unique = false,
    [property: JsonPropertyName("multiple")] bool Multiple = false,
    [property: JsonPropertyName("max_length")] int? MaxLength = null,
    [property: JsonPropertyName("minimum")] string? Minimum = null,
    [property: JsonPropertyName("maximum")] string? Maximum = null,
    [property: JsonPropertyName("apply_parents")] bool ApplyParents = false,
    [property: JsonPropertyName("term_set_id")] string? TermSetId = null,
    [property: JsonPropertyName("lookup_container_id")] string? LookupContainerId = null,
    [property: JsonPropertyName("default_value")] JsonElement? DefaultValue = null,
    [property: JsonPropertyName("currency_code")] string? CurrencyCode = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("search_weight")] string? SearchWeight = null);

public sealed record SchemaContentType(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("field_keys")] IReadOnlyList<string> FieldKeys,
    [property: JsonPropertyName("is_default")] bool IsDefault = false,
    [property: JsonPropertyName("rules")] IReadOnlyList<SchemaRule>? Rules = null);

public sealed record SchemaRule(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("other_field")] string? OtherField = null,
    [property: JsonPropertyName("when_field")] string? WhenField = null,
    [property: JsonPropertyName("when_value")] JsonElement? WhenValue = null);

public sealed record ContentHistoryView(long Number, IReadOnlyList<string> ChangedFields, ItemRevisionsRow Revision, SchemaRevisionsRow Schema);
