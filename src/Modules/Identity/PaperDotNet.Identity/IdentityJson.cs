using System.Text.Json;
using System.Text.Json.Serialization;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Features;

namespace PaperDotNet.Identity;

/// <summary>Every type the Identity API serializes, with source-generated metadata (Native AOT, ADR-0039).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(TokenError))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(UpdateMeRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(UserResponse))]
[JsonSerializable(typeof(List<UserResponse>))]
[JsonSerializable(typeof(Page<UserResponse>))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(UpdateUserRequest))]
[JsonSerializable(typeof(SetPasswordRequest))]
[JsonSerializable(typeof(GroupResponse))]
[JsonSerializable(typeof(List<GroupResponse>))]
[JsonSerializable(typeof(Page<GroupResponse>))]
[JsonSerializable(typeof(CreateGroupRequest))]
[JsonSerializable(typeof(UpdateGroupRequest))]
[JsonSerializable(typeof(AddGroupMemberRequest))]
[JsonSerializable(typeof(AddNestedGroupRequest))]
[JsonSerializable(typeof(RoleResponse))]
[JsonSerializable(typeof(List<RoleResponse>))]
[JsonSerializable(typeof(CreateRoleRequest))]
[JsonSerializable(typeof(UpdateRoleRequest))]
[JsonSerializable(typeof(RoleAssignmentRequest))]
[JsonSerializable(typeof(RoleAssignmentResponse))]
[JsonSerializable(typeof(List<RoleAssignmentResponse>))]
[JsonSerializable(typeof(ScopeResponse))]
[JsonSerializable(typeof(List<ScopeResponse>))]
[JsonSerializable(typeof(ApiTokenResponse))]
[JsonSerializable(typeof(List<ApiTokenResponse>))]
[JsonSerializable(typeof(CreateApiTokenRequest))]
[JsonSerializable(typeof(CreatedApiTokenResponse))]
[JsonSerializable(typeof(PreferencesResponse))]
[JsonSerializable(typeof(OrganizationResponse))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(PrincipalDeleted))]
internal sealed partial class IdentityJson : JsonSerializerContext;
