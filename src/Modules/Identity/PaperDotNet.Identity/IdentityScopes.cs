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
    public const string ApplicationManage = "application.manage";
    public const string OrganizationManage = "organization.manage";

    public static readonly ScopeDefinition[] All =
    [
        new(UserRead, "See users of the organization.", GrantedToMembers: true),
        new(UserManage, "Create, update, disable and delete users and reset their passwords."),
        new(GroupRead, "See groups and their members.", GrantedToMembers: true),
        new(GroupManage, "Create, rename and delete groups and manage their members."),
        new(RoleRead, "See roles and their assignments."),
        new(RoleManage, "Create, change and delete roles and assign them to users and groups."),
        new(ApplicationManage, "Register OAuth client applications and manage their secrets."),
        new(OrganizationManage, "Change the organization's default preferences."),
    ];
}
