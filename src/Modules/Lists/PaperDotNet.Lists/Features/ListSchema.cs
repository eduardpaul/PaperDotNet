using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// Who a Lists operation acts for (there is no ambient caller under Native AOT, ADR-0039): the tenant, the user (none
/// for the organization) and the causation depth; <see cref="System"/> skips permission checks.
/// </summary>
internal sealed record ListCaller(Guid TenantId, Guid? UserId, int Depth = 0, bool System = false)
{
    public ChangeActor Actor => new(TenantId, UserId, Depth);

    public static ListCaller From(ChangeActor actor, bool system = false) => new(actor.TenantId, actor.UserId, actor.Depth, system);
}

/// <summary>A content type with its fields read from JSON.</summary>
internal sealed record ContentTypeSchema(ContentType Entity, IReadOnlyList<FieldDefinition> Fields)
{
    public Guid Id => Entity.Id;

    public string Name => Entity.Name;

    public string? Key => Entity.Key;

    public static ContentTypeSchema From(ContentType entity) => new(entity, ListsJsonText.Fields(entity.Fields));
}

/// <summary>JSON text columns of the Lists tables, read and written with source-generated metadata.</summary>
internal static class ListsJsonText
{
    public static List<FieldDefinition> Fields(string json) => JsonSerializer.Deserialize(json, ListsJson.Default.ListFieldDefinition) ?? [];

    public static string Fields(IReadOnlyList<FieldDefinition> fields) => JsonSerializer.Serialize([.. fields], ListsJson.Default.ListFieldDefinition);

    public static List<Guid> Ids(string json) => JsonSerializer.Deserialize(json, ListsJson.Default.ListGuid) ?? [];

    public static string Ids(IEnumerable<Guid> ids) => JsonSerializer.Serialize(ids.ToList(), ListsJson.Default.ListGuid);
}

/// <summary>A list with its content types and the effective set of fields (union, by name).</summary>
internal sealed class ListSchema
{
    public ListSchema(ListDefinition list, IReadOnlyList<ContentTypeSchema> contentTypes, ListAccess access)
    {
        List = list;
        Access = access;
        ContentTypeIds = ListsJsonText.Ids(list.ContentTypeIds);
        ContentTypes = ContentTypeIds.Select(id => contentTypes.FirstOrDefault(c => c.Id == id)).OfType<ContentTypeSchema>().ToList();
        Fields = ContentTypes
            .SelectMany(c => c.Fields)
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public ListDefinition List { get; }

    public ListAccess Access { get; }

    /// <summary>The caller's access to the list itself.</summary>
    public WorkspaceAccessLevel Permission => Access.ListLevel;

    public IReadOnlyList<Guid> ContentTypeIds { get; }

    public IReadOnlyList<ContentTypeSchema> ContentTypes { get; }

    public ContentTypeSchema DefaultContentType => ContentTypes[0];

    public IReadOnlyDictionary<string, FieldDefinition> Fields { get; }

    public ContentTypeSchema? FindContentType(Guid id) => ContentTypes.FirstOrDefault(c => c.Id == id);

    /// <summary>A conflict message when adding <paramref name="fields"/> would give an existing field name a different type or multiplicity.</summary>
    public static string? FindConflict(IEnumerable<FieldDefinition> existing, IReadOnlyList<FieldDefinition> fields)
    {
        var byName = existing.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var clash = fields.FirstOrDefault(f => byName.TryGetValue(f.Name, out var other) && (other.Type != f.Type || other.AllowMultiple != f.AllowMultiple));
        return clash is null ? null : $"Field '{clash.Name}' already exists in the list with a different type.";
    }
}

/// <summary>
/// The caller's effective permissions in one list (IAM-07, ADR-0035): the level of every permission scope of the list
/// they reach (the list itself, and items with unique permissions). Workspace owners, administrators and system work
/// have full control.
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

    /// <summary>The scopes the caller has at least <paramref name="minimum"/> on; null with full control (every scope).</summary>
    public IReadOnlyList<Guid>? Scopes(WorkspaceAccessLevel minimum) =>
        FullControl ? null : [.. scopes.Where(s => s.Value >= minimum).Select(s => s.Key)];
}

/// <summary>Loads a list schema with the caller's access; null (404) when the list is not visible.</summary>
internal sealed class ListSchemaLoader(ListsDbContext db, IWorkspaceAccess workspaces, ItemAccess access)
{
    public async Task<ListSchema?> LoadAsync(ListCaller caller, Guid workspaceId, Guid listId, CancellationToken cancellationToken, bool tracking = false)
    {
        var workspaceLevel = caller.System ? WorkspaceAccessLevel.Manage
            : caller.UserId is { } userId ? await workspaces.GetPermissionAsync(caller.TenantId, userId, workspaceId, cancellationToken)
            : WorkspaceAccessLevel.None;
        if (workspaceLevel == WorkspaceAccessLevel.None || await FindListAsync(caller.TenantId, workspaceId, listId, tracking, cancellationToken) is not { } list)
        {
            return null;
        }

        var listAccess = workspaceLevel == WorkspaceAccessLevel.Manage
            ? ListAccess.Full(list.Id)
            : new ListAccess(list.Id, fullControl: false, await access.GetScopesAsync(caller, list.Id, cancellationToken));
        return listAccess.ListLevel == WorkspaceAccessLevel.None ? null : new ListSchema(list, await ContentTypesAsync(caller.TenantId, ListsJsonText.Ids(list.ContentTypeIds), cancellationToken), listAccess);
    }

    /// <summary>A list that is not deleted, from a workspace that is not deleted (checked by the caller's workspace access).</summary>
    public Task<ListDefinition?> FindListAsync(Guid tenantId, Guid workspaceId, Guid listId, bool tracking, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var workspace = workspaceId;
        var id = listId;
        var ct = cancellationToken;
        return tracking
            ? context.Lists.Where(l => l.TenantId == tenant && l.WorkspaceId == workspace && l.Id == id && l.DeletedAt == null).FirstOrDefaultAsync(ct)
            : context.Lists.AsNoTracking().Where(l => l.TenantId == tenant && l.WorkspaceId == workspace && l.Id == id && l.DeletedAt == null).FirstOrDefaultAsync(ct);
    }

    /// <summary>Content types by id, in the given order (unknown ids are left out).</summary>
    public async Task<List<ContentTypeSchema>> ContentTypesAsync(Guid tenantId, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new List<ContentTypeSchema>();
        foreach (var id in ids.Distinct())
        {
            if (await FindContentTypeAsync(tenantId, id, cancellationToken) is { } contentType)
            {
                result.Add(ContentTypeSchema.From(contentType));
            }
        }

        return result;
    }

    public Task<ContentType?> FindContentTypeAsync(Guid tenantId, Guid contentTypeId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var id = contentTypeId;
        var ct = cancellationToken;
        return context.ContentTypes.AsNoTracking().Where(c => c.TenantId == tenant && c.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Lists of the workspace the caller can see: all with full control, else those whose own scope gives them access.</summary>
    public async Task<List<ListDefinition>> VisibleListsAsync(ListCaller caller, Guid workspaceId, WorkspaceAccessLevel workspaceLevel, CancellationToken cancellationToken)
    {
        if (workspaceLevel == WorkspaceAccessLevel.None)
        {
            return [];
        }

        var context = db;
        var tenant = caller.TenantId;
        var workspace = workspaceId;
        var ct = cancellationToken;
        var lists = await context.Lists.AsNoTracking()
            .Where(l => l.TenantId == tenant && l.WorkspaceId == workspace && l.DeletedAt == null)
            .OrderBy(l => l.Name)
            .ToListAsync(ct);
        if (caller.System || workspaceLevel == WorkspaceAccessLevel.Manage)
        {
            return lists;
        }

        var principals = (await access.GetPrincipalIdsAsync(caller, cancellationToken)).ToHashSet();
        var entries = await context.AclEntries.AsNoTracking()
            .Where(e => e.TenantId == tenant && e.WorkspaceId == workspace && e.ScopeId == e.ListId)
            .ToListAsync(ct);
        var visible = entries.Where(e => principals.Contains(e.PrincipalId)).Select(e => e.ListId).ToHashSet();
        return [.. lists.Where(l => visible.Contains(l.Id))];
    }

    /// <summary>Lists of several workspaces the caller can see.</summary>
    public async Task<List<ListDefinition>> VisibleListsAsync(ListCaller caller, IReadOnlyCollection<WorkspaceMembership> memberships, CancellationToken cancellationToken)
    {
        var result = new List<ListDefinition>();
        foreach (var membership in memberships)
        {
            result.AddRange(await VisibleListsAsync(caller, membership.WorkspaceId, membership.Level, cancellationToken));
        }

        return [.. result.OrderBy(l => l.Name, StringComparer.Ordinal)];
    }
}

/// <summary>
/// Item permissions (ADR-0035): a caller's principals are their user id, their groups (including groups that contain
/// them) and the role principals of their workspaces (<see cref="WorkspaceRolePrincipals"/>). Cached for a minute per
/// tenant access generation (<see cref="AccessGeneration"/>).
/// </summary>
internal sealed class ItemAccess(ListsDbContext db, IUserDirectory users, IWorkspaceAccess workspaces, IMemoryCache cache) : IPrincipalSet
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<Guid>> GetPrincipalsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var key = (Kind: "principals", tenantId, userId, AccessGeneration.Current(tenantId));
        if (!cache.TryGetValue(key, out Guid[]? principals) || principals is null)
        {
            var groups = await users.GetGroupIdsAsync(tenantId, userId, cancellationToken);
            var memberships = await workspaces.GetMembershipsAsync(tenantId, userId, cancellationToken);
            principals = [userId, .. groups, .. memberships.Select(m => WorkspaceRolePrincipals.Id(m.WorkspaceId, m.Level))];
            cache.Set(key, principals, Lifetime);
        }

        return principals;
    }

    public async Task<IReadOnlyList<Guid>> GetPrincipalIdsAsync(ListCaller caller, CancellationToken cancellationToken) =>
        caller.UserId is { } userId ? await GetPrincipalsAsync(caller.TenantId, userId, cancellationToken) : [];

    /// <summary>The permission scopes of the list the caller reaches, with the highest level of their principals.</summary>
    public async Task<IReadOnlyDictionary<Guid, WorkspaceAccessLevel>> GetScopesAsync(ListCaller caller, Guid listId, CancellationToken cancellationToken)
    {
        var principals = (await GetPrincipalIdsAsync(caller, cancellationToken)).ToHashSet();
        if (principals.Count == 0)
        {
            return new Dictionary<Guid, WorkspaceAccessLevel>();
        }

        var context = db;
        var tenant = caller.TenantId;
        var list = listId;
        var ct = cancellationToken;
        var entries = await context.AclEntries.AsNoTracking().Where(e => e.TenantId == tenant && e.ListId == list).ToListAsync(ct);
        return entries
            .Where(e => principals.Contains(e.PrincipalId))
            .GroupBy(e => e.ScopeId)
            .ToDictionary(g => g.Key, g => (WorkspaceAccessLevel)g.Max(e => e.Level));
    }
}
