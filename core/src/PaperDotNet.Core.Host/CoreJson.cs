using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PaperDotNet.Core.Api;

namespace PaperDotNet.Core.Host;

/// <summary>
/// Every type the API and the message queue serialize, with source-generated metadata (no reflection under AOT).
/// Add request, response and event types here.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
// Parameter types, for the OpenAPI document.
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(TokenError))]
[JsonSerializable(typeof(MeDto))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(Page<UserDto>))]
[JsonSerializable(typeof(CreateUserRequest))]
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
[JsonSerializable(typeof(AuditEntryDto))]
[JsonSerializable(typeof(Page<AuditEntryDto>))]
[JsonSerializable(typeof(ListCreated))]
[JsonSerializable(typeof(ListDeleted))]
[JsonSerializable(typeof(ItemCreated))]
[JsonSerializable(typeof(ItemUpdated))]
[JsonSerializable(typeof(ItemDeleted))]
internal sealed partial class CoreJson : JsonSerializerContext;
