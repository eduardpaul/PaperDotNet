using System.Text.Json;
using System.Text.Json.Serialization;
using PaperDotNet.Api;
using PaperDotNet.Identity.Features;

namespace PaperDotNet.Identity;

/// <summary>Every type the Identity API serializes, with source-generated metadata (Native AOT, ADR-0039).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(TokenError))]
[JsonSerializable(typeof(MeDto))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(Page<UserDto>))]
[JsonSerializable(typeof(CreateUserRequest))]
internal sealed partial class IdentityJson : JsonSerializerContext;
