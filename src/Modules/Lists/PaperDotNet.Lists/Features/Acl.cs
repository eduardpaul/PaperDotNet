using PaperDotNet.Lists.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Builds and describes <see cref="AclEntry"/> rows (ADR-0035).</summary>
internal static class Acl
{
    /// <summary>The workspace role a principal type stands for, or null for users and groups.</summary>
    public static WorkspaceAccessLevel? RoleOf(AclPrincipalType type) => type switch
    {
        AclPrincipalType.WorkspaceVisitors => WorkspaceAccessLevel.Read,
        AclPrincipalType.WorkspaceMembers => WorkspaceAccessLevel.Contribute,
        AclPrincipalType.WorkspaceOwners => WorkspaceAccessLevel.Manage,
        _ => null,
    };

    /// <summary>Names of the workspace roles in templates (<c>Role="Members"</c>).</summary>
    public static readonly IReadOnlyDictionary<string, AclPrincipalType> RoleNames = new Dictionary<string, AclPrincipalType>(StringComparer.OrdinalIgnoreCase)
    {
        ["Visitors"] = AclPrincipalType.WorkspaceVisitors,
        ["Members"] = AclPrincipalType.WorkspaceMembers,
        ["Owners"] = AclPrincipalType.WorkspaceOwners,
    };

    public static string? RoleName(AclPrincipalType type) => RoleNames.FirstOrDefault(r => r.Value == type).Key;

    /// <summary>
    /// An entry of <paramref name="scopeId"/>. For workspace roles <paramref name="principalId"/> is ignored: the
    /// role principal of the list's workspace is used.
    /// </summary>
    public static AclEntry Entry(ListDefinition list, Guid scopeId, AclPrincipalType type, Guid principalId, WorkspaceAccessLevel level) => new()
    {
        ScopeId = scopeId,
        ListId = list.Id,
        WorkspaceId = list.WorkspaceId,
        PrincipalType = type,
        PrincipalId = RoleOf(type) is { } role ? WorkspaceRolePrincipals.Id(list.WorkspaceId, role) : principalId,
        Level = level,
    };

    /// <summary>The entries of an inheriting list: visitors read, members contribute, owners manage.</summary>
    public static IEnumerable<AclEntry> RoleEntries(ListDefinition list) => RoleEntries(list, list.Id);

    public static IEnumerable<AclEntry> RoleEntries(ListDefinition list, Guid scopeId) =>
    [
        Entry(list, scopeId, AclPrincipalType.WorkspaceVisitors, Guid.Empty, WorkspaceAccessLevel.Read),
        Entry(list, scopeId, AclPrincipalType.WorkspaceMembers, Guid.Empty, WorkspaceAccessLevel.Contribute),
        Owners(list, scopeId),
    ];

    /// <summary>The fixed entry every scope has: workspace owners have full control.</summary>
    public static AclEntry Owners(ListDefinition list, Guid scopeId) =>
        Entry(list, scopeId, AclPrincipalType.WorkspaceOwners, Guid.Empty, WorkspaceAccessLevel.Manage);

    /// <summary>
    /// The entries of <paramref name="scopeId"/> for <paramref name="grants"/>, with the owners entry added (it
    /// cannot be removed or lowered). One entry per principal.
    /// </summary>
    public static List<AclEntry> FromGrants(ListDefinition list, Guid scopeId, IEnumerable<PermissionGrantDto> grants) =>
    [
        .. grants
            .Where(g => g.PrincipalType != AclPrincipalType.WorkspaceOwners)
            .Select(g => Entry(list, scopeId, g.PrincipalType, g.PrincipalId, g.Level))
            .DistinctBy(e => e.PrincipalId),
        Owners(list, scopeId),
    ];

    /// <summary>
    /// Turns the tracked <paramref name="existing"/> entries of a scope into <paramref name="wanted"/>: changed levels
    /// are updated, missing principals removed and new ones added, so one save changes the scope at once.
    /// </summary>
    public static void Replace(ListsDbContext db, IEnumerable<AclEntry> existing, IEnumerable<AclEntry> wanted)
    {
        var remaining = wanted.ToDictionary(e => e.PrincipalId);
        foreach (var entry in existing)
        {
            if (remaining.Remove(entry.PrincipalId, out var next))
            {
                entry.Level = next.Level;
            }
            else
            {
                db.AclEntries.Remove(entry);
            }
        }

        db.AclEntries.AddRange(remaining.Values);
    }

    /// <summary>An entry as the API shows it: workspace roles carry the workspace id.</summary>
    public static PermissionGrantDto ToDto(AclEntry entry) =>
        new(entry.PrincipalType, RoleOf(entry.PrincipalType) is null ? entry.PrincipalId : entry.WorkspaceId, entry.Level);
}
