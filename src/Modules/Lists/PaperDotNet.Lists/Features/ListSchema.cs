using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>A list with its content types and the effective set of fields (union, by name).</summary>
internal sealed class ListSchema
{
    public ListSchema(ListDefinition list, IReadOnlyList<ContentType> contentTypes, ListAccess access)
    {
        List = list;
        Access = access;
        ContentTypes = list.ContentTypeIds
            .Select(id => contentTypes.FirstOrDefault(c => c.Id == id))
            .OfType<ContentType>()
            .ToList();
        Fields = ContentTypes
            .SelectMany(c => c.Fields)
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public ListDefinition List { get; }

    public ListAccess Access { get; }

    /// <summary>The current user's access to the list itself.</summary>
    public WorkspaceAccessLevel Permission => Access.ListLevel;

    public IReadOnlyList<ContentType> ContentTypes { get; }

    public ContentType DefaultContentType => ContentTypes[0];

    public IReadOnlyDictionary<string, FieldDefinition> Fields { get; }

    public ContentType? FindContentType(Guid id) => ContentTypes.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Returns a conflict message when adding <paramref name="contentType"/> would give an
    /// existing field name a different type or multiplicity.
    /// </summary>
    public static string? FindConflict(IEnumerable<FieldDefinition> existing, ContentType contentType)
    {
        var byName = existing.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var clash = contentType.Fields.FirstOrDefault(f =>
            byName.TryGetValue(f.Name, out var other) && (other.Type != f.Type || other.AllowMultiple != f.AllowMultiple));
        return clash is null ? null : $"Field '{clash.Name}' already exists in the list with a different type.";
    }
}

/// <summary>
/// The current user's effective permissions in one list (IAM-07): the list level
/// (workspace role, or the list's own grants) and the level of every item scope
/// with unique permissions. Workspace owners and administrators have full control.
/// </summary>
internal sealed class ListAccess(WorkspaceAccessLevel listLevel, bool fullControl, IReadOnlyDictionary<Guid, WorkspaceAccessLevel> scopes)
{
    public WorkspaceAccessLevel ListLevel { get; } = fullControl ? WorkspaceAccessLevel.Manage : listLevel;

    public bool FullControl { get; } = fullControl;

    /// <summary>Access to items of <paramref name="scopeId"/> (null = list scope).</summary>
    public WorkspaceAccessLevel Level(Guid? scopeId) =>
        FullControl ? WorkspaceAccessLevel.Manage
        : scopeId is { } id ? scopes.GetValueOrDefault(id, WorkspaceAccessLevel.None)
        : ListLevel;

    /// <summary>Items the user has at least <paramref name="minimum"/> on, as a query filter (null = all).</summary>
    public Expression<Func<ListItem, bool>>? Filter(WorkspaceAccessLevel minimum)
    {
        if (FullControl || (ListLevel >= minimum && scopes.Count == 0))
        {
            return null;
        }

        var allowed = scopes.Where(s => s.Value >= minimum).Select(s => s.Key).ToList();
        return ListLevel >= minimum
            ? i => i.ScopeId == null || allowed.Contains(i.ScopeId.Value)
            : i => i.ScopeId != null && allowed.Contains(i.ScopeId.Value);
    }
}

/// <summary>Loads a list schema with the user's access; null (404) when the list is not visible.</summary>
internal sealed class ListSchemaLoader(ListsDbContext db, IWorkspaceAccess workspaces, IUserDirectory users, ICurrentUser user)
{
    public async Task<ListSchema?> LoadAsync(Guid workspaceId, Guid listId, CancellationToken ct, bool tracking = false)
    {
        var workspaceLevel = await workspaces.GetPermissionAsync(workspaceId, ct);
        if (workspaceLevel == WorkspaceAccessLevel.None)
        {
            return null;
        }

        var lists = tracking ? db.Lists : db.Lists.AsNoTracking();
        var list = await lists.FirstOrDefaultAsync(l => l.Id == listId && l.WorkspaceId == workspaceId, ct);
        if (list is null)
        {
            return null;
        }

        var access = await GetAccessAsync(list, workspaceLevel, ct);
        if (access.ListLevel == WorkspaceAccessLevel.None)
        {
            return null;
        }

        var contentTypes = await db.ContentTypes.AsNoTracking().Where(c => list.ContentTypeIds.Contains(c.Id)).ToListAsync(ct);
        return new ListSchema(list, contentTypes, access);
    }

    /// <summary>Lists of the workspace the user can see (lists with unique permissions need a grant).</summary>
    public async Task<List<ListDefinition>> VisibleListsAsync(Guid workspaceId, WorkspaceAccessLevel workspaceLevel, CancellationToken ct)
    {
        var lists = await db.Lists.AsNoTracking().Where(l => l.WorkspaceId == workspaceId).OrderBy(l => l.Name).ToListAsync(ct);
        if (workspaceLevel == WorkspaceAccessLevel.Manage || lists.All(l => !l.HasUniquePermissions))
        {
            return lists;
        }

        var unique = lists.Where(l => l.HasUniquePermissions).Select(l => l.Id).ToList();
        var granted = (await GrantsForUserAsync(db.Grants.Where(g => unique.Contains(g.ObjectId)), ct)).Select(g => g.ObjectId).ToHashSet();
        return lists.Where(l => !l.HasUniquePermissions || granted.Contains(l.Id)).ToList();
    }

    private async Task<ListAccess> GetAccessAsync(ListDefinition list, WorkspaceAccessLevel workspaceLevel, CancellationToken ct)
    {
        if (workspaceLevel == WorkspaceAccessLevel.Manage)
        {
            return new ListAccess(WorkspaceAccessLevel.Manage, fullControl: true, new Dictionary<Guid, WorkspaceAccessLevel>());
        }

        var hasItemScopes = await db.Items.IgnoreQueryFilters([Persistence.QueryFilters.SoftDelete])
            .AnyAsync(i => i.ListId == list.Id && i.HasUniquePermissions, ct);
        if (!list.HasUniquePermissions && !hasItemScopes)
        {
            return new ListAccess(workspaceLevel, fullControl: false, new Dictionary<Guid, WorkspaceAccessLevel>());
        }

        var grants = await GrantsForUserAsync(db.Grants.Where(g => g.ListId == list.Id), ct);
        var levels = grants.GroupBy(g => g.ObjectId).ToDictionary(g => g.Key, g => g.Max(x => x.Level));
        var listLevel = list.HasUniquePermissions ? levels.GetValueOrDefault(list.Id, WorkspaceAccessLevel.None) : workspaceLevel;

        // Every unique item scope is listed, so scopes without a grant resolve to None.
        var scopes = await db.Items.IgnoreQueryFilters([Persistence.QueryFilters.SoftDelete])
            .Where(i => i.ListId == list.Id && i.HasUniquePermissions)
            .Select(i => i.Id)
            .ToListAsync(ct);
        return new ListAccess(listLevel, fullControl: false, scopes.ToDictionary(id => id, id => levels.GetValueOrDefault(id, WorkspaceAccessLevel.None)));
    }

    /// <summary>Grants that apply to the current user directly or through a group.</summary>
    private async Task<List<PermissionGrant>> GrantsForUserAsync(IQueryable<PermissionGrant> grants, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return [];
        }

        var groups = await users.GetGroupIdsAsync(userId, ct);
        return await grants.AsNoTracking()
            .Where(g => (g.PrincipalType == PrincipalType.User && g.PrincipalId == userId)
                        || (g.PrincipalType == PrincipalType.Group && groups.Contains(g.PrincipalId)))
            .ToListAsync(ct);
    }
}
