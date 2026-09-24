using PaperDotNet.Abstractions;

namespace PaperDotNet.Identity;

public static class IdentityScopes
{
    public const string UserRead = "user.read";
    public const string UserManage = "user.manage";
    public const string GroupRead = "group.read";
    public const string GroupManage = "group.manage";
    public const string RoleRead = "role.read";
    public const string RoleManage = "role.manage";

    public static readonly ScopeDefinition[] All =
    [
        new(UserRead, "See users of the organization.", GrantedToMembers: true),
        new(UserManage, "Create, update and disable users."),
        new(GroupRead, "See groups and their members.", GrantedToMembers: true),
        new(GroupManage, "Create groups and manage their members."),
        new(RoleRead, "See roles and their assignments."),
        new(RoleManage, "Create roles and assign them to users and groups."),
    ];
}
