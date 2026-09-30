using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Features;

namespace PaperDotNet.Lists;

/// <summary>Every type the Lists API, its events and its JSON columns serialize, with source-generated metadata (ADR-0039).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<FieldDefinition>))]
[JsonSerializable(typeof(List<Guid>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(ItemVersionResponse))]
[JsonSerializable(typeof(Page<ItemVersionResponse>))]
[JsonSerializable(typeof(RecycleBinItemResponse))]
[JsonSerializable(typeof(Page<RecycleBinItemResponse>))]
[JsonSerializable(typeof(FieldDefinitionDto))]
[JsonSerializable(typeof(ContentTypeResponse))]
[JsonSerializable(typeof(List<ContentTypeResponse>))]
[JsonSerializable(typeof(ContentTypeRequest))]
[JsonSerializable(typeof(FieldTypeResponse))]
[JsonSerializable(typeof(List<FieldTypeResponse>))]
[JsonSerializable(typeof(ListSummary))]
[JsonSerializable(typeof(List<ListSummary>))]
[JsonSerializable(typeof(ListResponse))]
[JsonSerializable(typeof(CreateListRequest))]
[JsonSerializable(typeof(UpdateListRequest))]
[JsonSerializable(typeof(AddListContentTypeRequest))]
[JsonSerializable(typeof(ItemResponse))]
[JsonSerializable(typeof(ItemPage))]
[JsonSerializable(typeof(CreateItemRequest))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(PermissionsResponse))]
[JsonSerializable(typeof(BreakInheritanceRequest))]
[JsonSerializable(typeof(ReplaceGrantsRequest))]
[JsonSerializable(typeof(CompleteFolderScopeChange))]
[JsonSerializable(typeof(ListCreated))]
[JsonSerializable(typeof(ListDeleted))]
[JsonSerializable(typeof(ItemAdded))]
[JsonSerializable(typeof(ItemUpdated))]
[JsonSerializable(typeof(ItemDeleted))]
[JsonSerializable(typeof(ItemRestored))]
[JsonSerializable(typeof(ItemPurged))]
internal sealed partial class ListsJson : JsonSerializerContext;
