using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;

namespace PaperDotNet.Lists;

/// <summary>Every type the Lists API and its events serialize, with source-generated metadata (Native AOT, ADR-0039).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FieldDefinition))]
[JsonSerializable(typeof(IReadOnlyList<FieldDefinition>))]
[JsonSerializable(typeof(ListDto))]
[JsonSerializable(typeof(Page<ListDto>))]
[JsonSerializable(typeof(CreateListRequest))]
[JsonSerializable(typeof(UpdateListRequest))]
[JsonSerializable(typeof(ItemDto))]
[JsonSerializable(typeof(Page<ItemDto>))]
[JsonSerializable(typeof(CreateItemRequest))]
[JsonSerializable(typeof(UpdateItemRequest))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(ListCreated))]
[JsonSerializable(typeof(ListDeleted))]
[JsonSerializable(typeof(ItemCreated))]
[JsonSerializable(typeof(ItemUpdated))]
[JsonSerializable(typeof(ItemDeleted))]
internal sealed partial class ListsJson : JsonSerializerContext;
