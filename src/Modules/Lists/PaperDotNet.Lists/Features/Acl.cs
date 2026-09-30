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

    /// <summary>An entry as the API shows it: for workspace roles the principal id is the workspace id.</summary>
    public static PermissionGrantDto ToDto(AclEntry entry) =>
        new(entry.PrincipalType, RoleOf(entry.PrincipalType) is null ? entry.PrincipalId : entry.WorkspaceId, (WorkspaceAccessLevel)entry.Level);

    /// <summary>The entries of <paramref name="scopeId"/> for <paramref name="grants"/>, always with workspace owners (Manage).</summary>
    public static List<AclEntry> FromGrants(ListDefinition list, Guid scopeId, IEnumerable<PermissionGrantDto> grants)
    {
        var entries = grants
            .Where(g => g.PrincipalType != AclPrincipalTypes.WorkspaceOwners)
            .Select(g => Entry(list, scopeId, g.PrincipalType, g.PrincipalId, g.Level))
            .ToList();
        entries.Add(Owners(list, scopeId));
        return entries;
    }

    /// <summary>Makes the tracked <paramref name="existing"/> entries of a scope equal to <paramref name="wanted"/>.</summary>
    public static void Replace(ListsDbContext db, IReadOnlyCollection<AclEntry> existing, IReadOnlyCollection<AclEntry> wanted)
    {
        var byPrincipal = wanted.ToDictionary(e => e.PrincipalId);
        foreach (var entry in existing)
        {
            if (byPrincipal.Remove(entry.PrincipalId, out var replacement))
            {
                entry.PrincipalType = replacement.PrincipalType;
                entry.Level = replacement.Level;
            }
            else
            {
                db.AclEntries.Remove(entry);
            }
        }

        db.AclEntries.AddRange(byPrincipal.Values);
    }
}
