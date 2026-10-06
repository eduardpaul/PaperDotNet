using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
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
/// The current user's effective permissions in one list (IAM-07, ADR-0035): the level of every permission scope of
/// the list they reach (the list itself, and items with unique permissions). Workspace owners and administrators have
/// full control.
/// </summary>
internal sealed class ListAccess(Guid listId, bool fullControl, IReadOnlyDictionary<Guid, WorkspaceAccessLevel> scopes)
{
    public static ListAccess Full(Guid listId) => new(listId, fullControl: true, new Dictionary<Guid, WorkspaceAccessLevel>());

    public bool FullControl { get; } = fullControl;

    /// <summary>Access to the list itself (and to the items that inherit from it).</summary>
    public WorkspaceAccessLevel ListLevel => Level(listId);

    /// <summary>Access to the items of <paramref name="scopeId"/>.</summary>
    public WorkspaceAccessLevel Level(Guid scopeId) =>
        FullControl ? WorkspaceAccessLevel.Manage : scopes.GetValueOrDefault(scopeId, WorkspaceAccessLevel.None);

    /// <summary>The scopes (of any list) the user has at least <paramref name="minimum"/> on; empty with full control.</summary>
    public IEnumerable<Guid> Scopes(WorkspaceAccessLevel minimum) =>
        FullControl ? [] : scopes.Where(s => s.Value >= minimum).Select(s => s.Key);

    /// <summary>
    /// Items the user has at least <paramref name="minimum"/> on, as a query filter (null = all). One parameter on
    /// both databases: an array on PostgreSQL, JSON on SQLite (plain <c>Contains</c> would send one per scope).
    /// </summary>
    public Expression<Func<ListItem, bool>>? Filter(WorkspaceAccessLevel minimum)
    {
        if (FullControl)
        {
            return null;
        }

        var allowed = scopes.Where(s => s.Value >= minimum).Select(s => s.Key).ToArray();
        return i => EF.Parameter(allowed).Contains(i.ScopeId);
    }
}

/// <summary>Loads a list schema with the user's access; null (404) when the list is not visible.</summary>
internal sealed class ListSchemaLoader(ListsDbContext db, IWorkspaceAccess workspaces, ItemAccess access)
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

        var listAccess = workspaceLevel == WorkspaceAccessLevel.Manage
            ? ListAccess.Full(list.Id)
            : new ListAccess(list.Id, fullControl: false, await access.GetScopesAsync([list.Id], ct));
        if (listAccess.ListLevel == WorkspaceAccessLevel.None)
        {
            return null;
        }

        var contentTypes = await db.ContentTypes.AsNoTracking().Where(c => list.ContentTypeIds.Contains(c.Id)).ToListAsync(ct);
        return new ListSchema(list, contentTypes, listAccess);
    }

    /// <summary>
    /// Loads the schemas of many lists at once (ADR-0035): one query each for the lists, their content types, the
    /// user's workspaces and their permission scopes, instead of all of that per list. Lists the user cannot read are
    /// left out; <paramref name="system"/> loads them all with full control.
    /// </summary>
    public async Task<List<ListSchema>> LoadManyAsync(IReadOnlyCollection<Guid> listIds, bool system, CancellationToken ct)
    {
        var ids = listIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var lists = await db.Lists.AsNoTracking().Where(l => EF.Parameter(ids).Contains(l.Id)).ToListAsync(ct);
        var typeIds = lists.SelectMany(l => l.ContentTypeIds).Distinct().ToArray();
        var contentTypes = await db.ContentTypes.AsNoTracking().Where(c => EF.Parameter(typeIds).Contains(c.Id)).ToListAsync(ct);
        if (system)
        {
            return [.. lists.Select(l => new ListSchema(l, contentTypes, ListAccess.Full(l.Id)))];
        }

        var levels = (await workspaces.GetMyWorkspacesAsync(ct)).ToDictionary(m => m.WorkspaceId, m => m.Level);
        var scopes = await access.GetScopesAsync(ids, ct);
        var result = new List<ListSchema>();
        foreach (var list in lists)
        {
            var level = levels.GetValueOrDefault(list.WorkspaceId, WorkspaceAccessLevel.None);
            var listAccess = level == WorkspaceAccessLevel.Manage ? ListAccess.Full(list.Id) : new ListAccess(list.Id, fullControl: false, scopes);
            if (level != WorkspaceAccessLevel.None && listAccess.ListLevel != WorkspaceAccessLevel.None)
            {
                result.Add(new ListSchema(list, contentTypes, listAccess));
            }
        }

        return result;
    }

    /// <summary>Loads a list of the current tenant with full control, without checking the user (system work).</summary>
    public async Task<ListSchema?> LoadAsSystemAsync(Guid workspaceId, Guid listId, CancellationToken ct, bool tracking = false)
    {
        var lists = tracking ? db.Lists : db.Lists.AsNoTracking();
        var list = await lists.FirstOrDefaultAsync(l => l.Id == listId && l.WorkspaceId == workspaceId, ct);
        if (list is null)
        {
            return null;
        }

        var contentTypes = await db.ContentTypes.AsNoTracking().Where(c => list.ContentTypeIds.Contains(c.Id)).ToListAsync(ct);
        return new ListSchema(list, contentTypes, ListAccess.Full(list.Id));
    }

    /// <summary>Names and keys of content types, so a caller can skip lists before loading a full schema.</summary>
    public async Task<Dictionary<Guid, (string Name, string? Key)>> ContentTypesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var idList = ids.Distinct().ToList();
        return idList.Count == 0
            ? []
            : await db.ContentTypes.AsNoTracking().Where(c => idList.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => (c.Name, c.Key), ct);
    }

    /// <summary>Lists of the workspace the user can see: those whose own scope gives them access.</summary>
    public Task<List<ListDefinition>> VisibleListsAsync(Guid workspaceId, WorkspaceAccessLevel workspaceLevel, CancellationToken ct) =>
        VisibleListsAsync([new WorkspaceMembership(workspaceId, workspaceLevel)], ct);

    /// <summary>Lists of several workspaces the user can see, in two queries whatever the number of workspaces.</summary>
    public async Task<List<ListDefinition>> VisibleListsAsync(IReadOnlyCollection<WorkspaceMembership> memberships, CancellationToken ct)
    {
        var workspaceIds = memberships.Where(m => m.Level > WorkspaceAccessLevel.None).Select(m => m.WorkspaceId).ToArray();
        var lists = workspaceIds.Length == 0
            ? []
            : await db.Lists.AsNoTracking().Where(l => EF.Parameter(workspaceIds).Contains(l.WorkspaceId)).OrderBy(l => l.Name).ToListAsync(ct);
        var managed = memberships.Where(m => m.Level == WorkspaceAccessLevel.Manage).Select(m => m.WorkspaceId).ToHashSet();
        if (lists.All(l => managed.Contains(l.WorkspaceId)))
        {
            return lists;
        }

        var principals = await access.GetPrincipalIdsAsync(ct);
        var listIds = lists.Where(l => !managed.Contains(l.WorkspaceId)).Select(l => l.Id).ToArray();
        var visible = (await db.AclEntries.AsNoTracking()
                .Where(e => EF.Parameter(principals).Contains(e.PrincipalId) && EF.Parameter(listIds).Contains(e.ListId) && e.ScopeId == e.ListId)
                .Select(e => e.ListId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        return lists.Where(l => managed.Contains(l.WorkspaceId) || visible.Contains(l.Id)).ToList();
    }
}

/// <summary>
/// <see cref="IItemAccess"/>: the principal set is cached per tenant and user, and evicted with
/// <see cref="AccessCacheTags.Principals"/> when groups, roles or workspace memberships change.
/// </summary>
internal sealed class ItemAccess(
    ListsDbContext db, IUserDirectory users, IWorkspaceAccess workspaces, ICurrentUser user, ITenantContext tenant, HybridCache cache) : IItemAccess
{
    public async Task<IReadOnlySet<Guid>> FilterReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = itemIds.Distinct().ToArray();
        var found = await db.Items.AsNoTracking().Where(i => !i.IsFolder && EF.Parameter(ids).Contains(i.Id))
            .Select(i => new { i.Id, i.ListId, i.ScopeId }).ToListAsync(cancellationToken);
        if (found.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var scopes = await GetScopesAsync([.. found.Select(i => i.ListId).Distinct()], cancellationToken);
        return found.Where(i => scopes.TryGetValue(i.ScopeId, out var level) && level >= WorkspaceAccessLevel.Read).Select(i => i.Id).ToHashSet();
    }

    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromMinutes(1) };
    private Guid[]? principals;

    public async Task<IReadOnlyList<Guid>> GetPrincipalsAsync(CancellationToken cancellationToken) => await GetPrincipalIdsAsync(cancellationToken);

    public async Task<Guid[]> GetPrincipalIdsAsync(CancellationToken cancellationToken)
    {
        if (principals is not null)
        {
            return principals;
        }

        if (user.UserId is not { } userId || tenant.TenantId is not { } tenantId)
        {
            return principals = [];
        }

        // Without a cancellable token HybridCache runs the factory in this call. With one, it queues the factory on the
        // thread pool, where the request's tenant and user (ambient) are missing and every query would find nothing.
        principals = await cache.GetOrCreateAsync(
            $"lists:principals:{tenantId}:{userId}",
            async ct => await LoadAsync(userId, ct),
            CacheOptions,
            [AccessCacheTags.Principals(tenantId)],
            CancellationToken.None);
        return principals;
    }

    public async Task<IReadOnlyDictionary<Guid, WorkspaceAccessLevel>> GetScopesAsync(IReadOnlyCollection<Guid>? listIds, CancellationToken cancellationToken)
    {
        var ids = await GetPrincipalIdsAsync(cancellationToken);
        if (ids.Length == 0 || listIds is { Count: 0 })
        {
            return new Dictionary<Guid, WorkspaceAccessLevel>();
        }

        var entries = db.AclEntries.AsNoTracking().Where(e => EF.Parameter(ids).Contains(e.PrincipalId));
        if (listIds is { Count: 1 })
        {
            var listId = listIds.First();
            entries = entries.Where(e => e.ListId == listId);
        }
        else if (listIds is not null)
        {
            var lists = listIds.ToArray();
            entries = entries.Where(e => EF.Parameter(lists).Contains(e.ListId));
        }

        return await entries
            .GroupBy(e => e.ScopeId)
            .Select(g => new { g.Key, Level = g.Max(e => e.Level) })
            .ToDictionaryAsync(g => g.Key, g => g.Level, cancellationToken);
    }

    private async Task<Guid[]> LoadAsync(Guid userId, CancellationToken ct)
    {
        var groups = await users.GetGroupIdsAsync(userId, ct);
        var memberships = await workspaces.GetMembershipsAsync(userId, ct);
        return [userId, .. groups, .. memberships.Select(m => WorkspaceRolePrincipals.Id(m.WorkspaceId, m.Level))];
    }
}
