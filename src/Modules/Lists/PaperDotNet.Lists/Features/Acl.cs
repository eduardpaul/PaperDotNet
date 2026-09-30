using PaperDotNet.Lists.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Builds <see cref="AclEntry"/> rows (ADR-0035).</summary>
internal static class Acl
{
    /// <summary>The workspace role a principal type stands for, or null for users and groups.</summary>
    public static WorkspaceAccessLevel? RoleOf(string type) => type switch
    {
        AclPrincipalTypes.WorkspaceVisitors => WorkspaceAccessLevel.Read,
        AclPrincipalTypes.WorkspaceMembers => WorkspaceAccessLevel.Contribute,
        AclPrincipalTypes.WorkspaceOwners => WorkspaceAccessLevel.Manage,
        _ => null,
    };

    /// <summary>
    /// An entry of <paramref name="scopeId"/>. For workspace roles <paramref name="principalId"/> is ignored: the role
    /// principal of the list's workspace is used.
    /// </summary>
    public static AclEntry Entry(ListDefinition list, Guid scopeId, string type, Guid principalId, WorkspaceAccessLevel level) => new()
    {
        TenantId = list.TenantId,
        ScopeId = scopeId,
        ListId = list.Id,
        WorkspaceId = list.WorkspaceId,
        PrincipalType = type,
        PrincipalId = RoleOf(type) is { } role ? WorkspaceRolePrincipals.Id(list.WorkspaceId, role) : principalId,
        Level = (int)level,
    };

    /// <summary>The entries of an inheriting list: visitors read, members contribute, owners manage.</summary>
    public static IEnumerable<AclEntry> RoleEntries(ListDefinition list) => RoleEntries(list, list.Id);

    public static IEnumerable<AclEntry> RoleEntries(ListDefinition list, Guid scopeId) =>
    [
        Entry(list, scopeId, AclPrincipalTypes.WorkspaceVisitors, Guid.Empty, WorkspaceAccessLevel.Read),
        Entry(list, scopeId, AclPrincipalTypes.WorkspaceMembers, Guid.Empty, WorkspaceAccessLevel.Contribute),
        Owners(list, scopeId),
    ];

    /// <summary>The fixed entry every scope has: workspace owners have full control.</summary>
    public static AclEntry Owners(ListDefinition list, Guid scopeId) =>
        Entry(list, scopeId, AclPrincipalTypes.WorkspaceOwners, Guid.Empty, WorkspaceAccessLevel.Manage);
}
