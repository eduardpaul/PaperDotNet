using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

public sealed record PermissionGrantDto(PrincipalType PrincipalType, [property: Required] Guid PrincipalId, WorkspaceAccessLevel Level);

/// <summary>
/// Permissions of a list or item. <c>inheritsFrom</c> names where they come from:
/// <c>workspace</c>, <c>list</c> or <c>item</c> (with <c>inheritsFromId</c>), or null when unique.
/// Grants are shown to managers only.
/// </summary>
public sealed record PermissionsResponse(
    bool HasUniquePermissions, string? InheritsFrom, Guid? InheritsFromId, WorkspaceAccessLevel EffectiveLevel, IReadOnlyList<PermissionGrantDto>? Grants);

public sealed record BreakInheritanceRequest(bool CopyGrants = true);

public sealed record ReplaceGrantsRequest([property: Required] IReadOnlyList<PermissionGrantDto> Grants);

/// <summary>
/// Permission inheritance (IAM-07): workspace → list → folder → item. Breaking
/// inheritance copies the inherited grants (by default); resetting returns to the parent's.
/// </summary>
internal static class PermissionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var list = endpoints.MapV1Group($"{ListEndpoints.Route}/{{listId:guid}}/permissions", "Permissions");
        list.MapGet("", GetListAsync).RequireScope(ListScopes.Read).WithName("GetListPermissions");
        list.MapPost("/breakInheritance", BreakListAsync).RequireScope(ListScopes.Read).WithName("BreakListInheritance");
        list.MapPost("/resetInheritance", ResetListAsync).RequireScope(ListScopes.Read).WithName("ResetListInheritance");
        list.MapPut("/grants", ReplaceListGrantsAsync).RequireScope(ListScopes.Read).WithName("ReplaceListGrants");

        var item = endpoints.MapV1Group($"{ListEndpoints.Route}/{{listId:guid}}/items/{{itemId:guid}}/permissions", "Permissions");
        item.MapGet("", GetItemAsync).RequireScope(ListScopes.Read).WithName("GetItemPermissions");
        item.MapPost("/breakInheritance", BreakItemAsync).RequireScope(ListScopes.Read).WithName("BreakItemInheritance");
        item.MapPost("/resetInheritance", ResetItemAsync).RequireScope(ListScopes.Read).WithName("ResetItemInheritance");
        item.MapPut("/grants", ReplaceItemGrantsAsync).RequireScope(ListScopes.Read).WithName("ReplaceItemGrants");
    }

    // ---- Lists --------------------------------------------------------------

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> GetListAsync(
        Guid workspaceId, Guid listId, ListSchemaLoader loader, ListsDbContext db, IWorkspaceAccess workspaces, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        var list = schema.List;
        var grants = schema.Permission == WorkspaceAccessLevel.Manage ? await ScopeGrantsAsync(db, workspaces, list, null, ct) : null;
        return TypedResults.Ok(new PermissionsResponse(
            list.HasUniquePermissions, list.HasUniquePermissions ? null : "workspace", list.HasUniquePermissions ? null : list.WorkspaceId, schema.Permission, grants));
    }

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> BreakListAsync(
        Guid workspaceId, Guid listId, BreakInheritanceRequest? request, ListSchemaLoader loader, ListsDbContext db,
        IWorkspaceAccess workspaces, ICurrentUser user, IOutbox outbox, ITenantContext tenant, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct, tracking: true);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (schema.List.HasUniquePermissions)
        {
            return ApiErrors.Conflict("alreadyUnique", "The list already has unique permissions.");
        }

        var grants = request?.CopyGrants != false ? await ScopeGrantsAsync(db, workspaces, schema.List, null, ct) : [];
        AddGrants(db, schema.List.Id, schema.List.Id, WithManager(grants, user, schema.Access));
        schema.List.HasUniquePermissions = true;
        await outbox.SaveChangesAsync(db, [ListIndexInvalidated.For(tenant, user, listId)], cancellationToken: ct);
        return TypedResults.Ok(new PermissionsResponse(true, null, null, WorkspaceAccessLevel.Manage, await ScopeGrantsAsync(db, workspaces, schema.List, null, ct)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetListAsync(
        Guid workspaceId, Guid listId, ListSchemaLoader loader, ListsDbContext db, IOutbox outbox, ITenantContext tenant, ICurrentUser user, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct, tracking: true);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (schema.List.HasUniquePermissions)
        {
            db.Grants.RemoveRange(await db.Grants.Where(g => g.ObjectId == listId).ToListAsync(ct));
            schema.List.HasUniquePermissions = false;
            await outbox.SaveChangesAsync(db, [ListIndexInvalidated.For(tenant, user, listId)], cancellationToken: ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PermissionsResponse>, ValidationProblem, ProblemHttpResult>> ReplaceListGrantsAsync(
        Guid workspaceId, Guid listId, ReplaceGrantsRequest request, ListSchemaLoader loader, ListsDbContext db,
        IWorkspaceAccess workspaces, IUserDirectory users, IOutbox outbox, ITenantContext tenant, ICurrentUser user, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (!schema.List.HasUniquePermissions)
        {
            return ApiErrors.Conflict("inheritsPermissions", "Break inheritance before changing grants.");
        }

        if (await ReplaceAsync(db, users, listId, listId, request, ListIndexInvalidated.For(tenant, user, listId), outbox, ct) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(new PermissionsResponse(true, null, null, schema.Permission, await ScopeGrantsAsync(db, workspaces, schema.List, null, ct)));
    }

    // ---- Items --------------------------------------------------------------

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> GetItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, IWorkspaceAccess workspaces, CancellationToken ct)
    {
        var (schema, item) = await LoadItemAsync(workspaceId, listId, itemId, loader, db, tracking: false, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        var level = schema!.Access.Level(item.ScopeId);
        var grants = level == WorkspaceAccessLevel.Manage ? await ScopeGrantsAsync(db, workspaces, schema.List, item.ScopeId, ct) : null;
        var (source, sourceId) = item.HasUniquePermissions ? ((string?)null, (Guid?)null)
            : item.ScopeId is { } scope ? ("item", scope)
            : schema.List.HasUniquePermissions ? ("list", schema.List.Id)
            : ("workspace", schema.List.WorkspaceId);
        return TypedResults.Ok(new PermissionsResponse(item.HasUniquePermissions, source, sourceId, level, grants));
    }

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> BreakItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, BreakInheritanceRequest? request, ListSchemaLoader loader, ListsDbContext db,
        IWorkspaceAccess workspaces, ICurrentUser user, IOutbox outbox, ITenantContext tenant, CancellationToken ct)
    {
        var (schema, item) = await LoadItemAsync(workspaceId, listId, itemId, loader, db, tracking: true, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema!.Access.Level(item.ScopeId) < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (item.HasUniquePermissions)
        {
            return ApiErrors.Conflict("alreadyUnique", "The item already has unique permissions.");
        }

        var oldScope = item.ScopeId;
        var grants = request?.CopyGrants != false ? await ScopeGrantsAsync(db, workspaces, schema.List, oldScope, ct) : [];
        AddGrants(db, listId, item.Id, WithManager(grants, user, schema.Access));
        item.HasUniquePermissions = true;
        item.ScopeId = item.Id;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.SaveChangesAsync(ct);
            if (item.IsFolder)
            {
                await ScopeTree.ReassignAsync(db, item.Id, oldScope, item.Id, ct);
            }

            await transaction.CommitAsync(ct);
        }

        await outbox.SaveChangesAsync(db, [ListIndexInvalidated.For(tenant, user, listId)], cancellationToken: ct);

        return TypedResults.Ok(new PermissionsResponse(true, null, null, WorkspaceAccessLevel.Manage, await ScopeGrantsAsync(db, workspaces, schema.List, item.Id, ct)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, IOutbox outbox, ITenantContext tenant, ICurrentUser user,
        CancellationToken ct)
    {
        var (schema, item) = await LoadItemAsync(workspaceId, listId, itemId, loader, db, tracking: true, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema!.Access.Level(item.ScopeId) < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (!item.HasUniquePermissions)
        {
            return TypedResults.NoContent();
        }

        var parentScope = item.ParentId is { } parentId
            ? await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).Where(i => i.Id == parentId).Select(i => i.ScopeId).FirstOrDefaultAsync(ct)
            : null;
        db.Grants.RemoveRange(await db.Grants.Where(g => g.ObjectId == itemId).ToListAsync(ct));
        item.HasUniquePermissions = false;
        item.ScopeId = parentScope;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.SaveChangesAsync(ct);
            if (item.IsFolder)
            {
                await ScopeTree.ReassignAsync(db, item.Id, item.Id, parentScope, ct);
            }

            await transaction.CommitAsync(ct);
        }

        await outbox.SaveChangesAsync(db, [ListIndexInvalidated.For(tenant, user, listId)], cancellationToken: ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PermissionsResponse>, ValidationProblem, ProblemHttpResult>> ReplaceItemGrantsAsync(
        Guid workspaceId, Guid listId, Guid itemId, ReplaceGrantsRequest request, ListSchemaLoader loader, ListsDbContext db,
        IWorkspaceAccess workspaces, IUserDirectory users, IOutbox outbox, ITenantContext tenant, ICurrentUser user, CancellationToken ct)
    {
        var (schema, item) = await LoadItemAsync(workspaceId, listId, itemId, loader, db, tracking: false, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        var level = schema!.Access.Level(item.ScopeId);
        if (level < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (!item.HasUniquePermissions)
        {
            return ApiErrors.Conflict("inheritsPermissions", "Break inheritance before changing grants.");
        }

        if (await ReplaceAsync(db, users, listId, itemId, request, ListIndexInvalidated.For(tenant, user, listId), outbox, ct) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(new PermissionsResponse(true, null, null, level, await ScopeGrantsAsync(db, workspaces, schema.List, item.Id, ct)));
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>A visible item (404 otherwise), including folders.</summary>
    private static async Task<(ListSchema? Schema, ListItem? Item)> LoadItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, bool tracking, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return (null, null);
        }

        var items = tracking ? db.Items : db.Items.AsNoTracking();
        var item = await items.FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct);
        return item is null || schema.Access.Level(item.ScopeId) < WorkspaceAccessLevel.Read ? (schema, null) : (schema, item);
    }

    /// <summary>The grants in effect for a scope: an item's, the list's, or the workspace members'.</summary>
    private static async Task<List<PermissionGrantDto>> ScopeGrantsAsync(
        ListsDbContext db, IWorkspaceAccess workspaces, ListDefinition list, Guid? scopeId, CancellationToken ct)
    {
        Guid? objectId = scopeId ?? (list.HasUniquePermissions ? list.Id : null);
        if (objectId is { } id)
        {
            return await db.Grants.AsNoTracking()
                .Where(g => g.ObjectId == id)
                .OrderBy(g => g.PrincipalType).ThenBy(g => g.PrincipalId)
                .Select(g => new PermissionGrantDto(g.PrincipalType, g.PrincipalId, g.Level))
                .ToListAsync(ct);
        }

        return (await workspaces.GetMembersAsync(list.WorkspaceId, ct))
            .OrderBy(m => m.UserId)
            .Select(m => new PermissionGrantDto(PrincipalType.User, m.UserId, m.Level))
            .ToList();
    }

    /// <summary>Makes sure the user breaking inheritance keeps managing the object (unless they have full control anyway).</summary>
    private static List<PermissionGrantDto> WithManager(List<PermissionGrantDto> grants, ICurrentUser user, ListAccess access)
    {
        if (access.FullControl || user.UserId is not { } userId)
        {
            return grants;
        }

        return [.. grants.Where(g => !(g.PrincipalType == PrincipalType.User && g.PrincipalId == userId)), new PermissionGrantDto(PrincipalType.User, userId, WorkspaceAccessLevel.Manage)];
    }

    private static void AddGrants(ListsDbContext db, Guid listId, Guid objectId, IEnumerable<PermissionGrantDto> grants)
    {
        foreach (var grant in grants.DistinctBy(g => (g.PrincipalType, g.PrincipalId)))
        {
            db.Grants.Add(new PermissionGrant
            {
                Id = Ids.New(),
                ListId = listId,
                ObjectId = objectId,
                PrincipalType = grant.PrincipalType,
                PrincipalId = grant.PrincipalId,
                Level = grant.Level,
            });
        }
    }

    private static async Task<ValidationProblem?> ReplaceAsync(
        ListsDbContext db, IUserDirectory users, Guid listId, Guid objectId, ReplaceGrantsRequest request,
        ListIndexInvalidated invalidated, IOutbox outbox, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var grant in request.Grants ?? [])
        {
            if (grant.Level is not (WorkspaceAccessLevel.Read or WorkspaceAccessLevel.Contribute or WorkspaceAccessLevel.Manage))
            {
                errors.Add($"{grant.PrincipalId}: level must be read, contribute or manage.");
            }
            else if (grant.PrincipalType == PrincipalType.User ? !await users.IsActiveAsync(grant.PrincipalId, ct) : !await users.GroupExistsAsync(grant.PrincipalId, ct))
            {
                errors.Add($"{grant.PrincipalId}: unknown {(grant.PrincipalType == PrincipalType.User ? "user" : "group")}.");
            }
        }

        if ((request.Grants ?? []).GroupBy(g => (g.PrincipalType, g.PrincipalId)).Any(g => g.Count() > 1))
        {
            errors.Add("Each principal can appear once.");
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["grants"] = [.. errors] });
        }

        db.Grants.RemoveRange(await db.Grants.Where(g => g.ObjectId == objectId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        AddGrants(db, listId, objectId, request.Grants ?? []);
        await outbox.SaveChangesAsync(db, [invalidated], cancellationToken: ct);
        return null;
    }
}

/// <summary>Keeps the security scope of items under a folder in sync with the folder.</summary>
internal static class ScopeTree
{
    private const int MaxDepth = 64;

    /// <summary>
    /// Items below <paramref name="folderId"/> that inherit (scope <paramref name="oldScope"/>)
    /// move to <paramref name="newScope"/>. Folders with unique permissions stop the walk.
    /// Includes items in the recycle bin.
    /// </summary>
    public static async Task ReassignAsync(ListsDbContext db, Guid folderId, Guid? oldScope, Guid? newScope, CancellationToken ct)
    {
        var all = db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]);
        List<Guid> frontier = [folderId];
        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var current = frontier;
            var children = await all
                .Where(i => i.ParentId != null && current.Contains(i.ParentId.Value) && !i.HasUniquePermissions && i.ScopeId == oldScope)
                .Select(i => new { i.Id, i.IsFolder })
                .ToListAsync(ct);
            var ids = children.Select(c => c.Id).ToList();
            if (ids.Count > 0)
            {
                await all.Where(i => ids.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.ScopeId, newScope), ct);
            }

            frontier = children.Where(c => c.IsFolder).Select(c => c.Id).ToList();
        }
    }
}
