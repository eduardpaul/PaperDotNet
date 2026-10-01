using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Messaging;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// A permission entry: <c>principalType</c> is <c>user</c>, <c>group</c>, <c>workspaceVisitors</c>,
/// <c>workspaceMembers</c> or <c>workspaceOwners</c>; for workspace roles the principal id is the workspace id.
/// </summary>
public sealed record PermissionGrantDto(string PrincipalType, Guid PrincipalId, WorkspaceAccessLevel Level);

/// <summary>
/// Permissions of a list or item. <c>inheritsFrom</c> names where they come from: <c>workspace</c>, <c>list</c> or
/// <c>item</c> (with <c>inheritsFromId</c>), or null when unique. Grants are shown to managers only.
/// </summary>
public sealed record PermissionsResponse(
    bool HasUniquePermissions, string? InheritsFrom, Guid? InheritsFromId, WorkspaceAccessLevel EffectiveLevel, IReadOnlyList<PermissionGrantDto>? Grants);

public sealed record BreakInheritanceRequest(bool CopyGrants = true);

public sealed record ReplaceGrantsRequest(IReadOnlyList<PermissionGrantDto>? Grants);

/// <summary>
/// Permission inheritance (IAM-07, ADR-0035): workspace roles → list → folder → item. A list inherits through entries
/// for the workspace roles; breaking inheritance copies the inherited entries (by default), resetting returns to the
/// parent's. Workspace owners always keep Manage.
/// </summary>
internal static class PermissionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var list = app.MapGroup($"{ListEndpoints.Route}/{{listId:guid}}/permissions").WithTags("Permissions");
        list.MapGet("", GetListAsync).RequireScope(ListScopes.Read).WithName("GetListPermissions");
        list.MapPost("/breakInheritance", BreakListAsync).RequireScope(ListScopes.Read).WithName("BreakListInheritance");
        list.MapPost("/resetInheritance", ResetListAsync).RequireScope(ListScopes.Read).WithName("ResetListInheritance");
        list.MapPut("/grants", ReplaceListGrantsAsync).RequireScope(ListScopes.Read).WithName("ReplaceListGrants");

        var item = app.MapGroup($"{ListEndpoints.Route}/{{listId:guid}}/items/{{itemId:guid}}/permissions").WithTags("Permissions");
        item.MapGet("", GetItemAsync).RequireScope(ListScopes.Read).WithName("GetItemPermissions");
        item.MapPost("/breakInheritance", BreakItemAsync).RequireScope(ListScopes.Read).WithName("BreakItemInheritance");
        item.MapPost("/resetInheritance", ResetItemAsync).RequireScope(ListScopes.Read).WithName("ResetItemInheritance");
        item.MapPut("/grants", ReplaceItemGrantsAsync).RequireScope(ListScopes.Read).WithName("ReplaceItemGrants");
    }

    // ---- Lists --------------------------------------------------------------

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> GetListAsync(
        Guid workspaceId, Guid listId, Caller caller, ListSchemaLoader loader, ListsDbContext db, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        var list = schema.List;
        var level = schema.Access.ListLevel;
        var grants = level == WorkspaceAccessLevel.Manage ? await ScopeGrantsAsync(db, caller.TenantId, list.Id, cancellationToken) : null;
        return TypedResults.Ok(new PermissionsResponse(
            list.HasUniquePermissions, list.HasUniquePermissions ? null : "workspace", list.HasUniquePermissions ? null : list.WorkspaceId, level, grants));
    }

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> BreakListAsync(
        Guid workspaceId, Guid listId, BreakInheritanceRequest? request, Caller caller, ListSchemaLoader loader, ListsDbContext db,
        CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        var schema = await loader.LoadAsync(listCaller, workspaceId, listId, cancellationToken, tracking: true);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Access.ListLevel < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (schema.List.HasUniquePermissions)
        {
            return ApiErrors.Conflict("alreadyUnique", "The list already has unique permissions.");
        }

        // The list's entries stay as they are (a copy of the inherited ones) unless they should start empty.
        var existing = await TrackedEntriesAsync(db, caller.TenantId, listId, cancellationToken);
        List<PermissionGrantDto> grants = request?.CopyGrants != false ? [.. existing.Select(Acl.ToDto)] : [];
        Acl.Replace(db, existing, Acl.FromGrants(schema.List, listId, WithManager(grants, listCaller, schema.Access)));
        schema.List.HasUniquePermissions = true;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new PermissionsResponse(true, null, null, WorkspaceAccessLevel.Manage, await ScopeGrantsAsync(db, caller.TenantId, listId, cancellationToken)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetListAsync(
        Guid workspaceId, Guid listId, Caller caller, ListSchemaLoader loader, ListsDbContext db, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken, tracking: true);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Access.ListLevel < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (schema.List.HasUniquePermissions)
        {
            Acl.Replace(db, await TrackedEntriesAsync(db, caller.TenantId, listId, cancellationToken), [.. Acl.RoleEntries(schema.List)]);
            schema.List.HasUniquePermissions = false;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PermissionsResponse>, ValidationProblem, ProblemHttpResult>> ReplaceListGrantsAsync(
        Guid workspaceId, Guid listId, ReplaceGrantsRequest request, Caller caller, ListSchemaLoader loader, ListsDbContext db,
        IUserDirectory users, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Access.ListLevel < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (!schema.List.HasUniquePermissions)
        {
            return ApiErrors.Conflict("inheritsPermissions", "Break inheritance before changing grants.");
        }

        if (await ReplaceAsync(db, users, schema.List, listId, request, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(new PermissionsResponse(true, null, null, schema.Access.ListLevel, await ScopeGrantsAsync(db, caller.TenantId, listId, cancellationToken)));
    }

    // ---- Items --------------------------------------------------------------

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> GetItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, ListSchemaLoader loader, ListsDbContext db, CancellationToken cancellationToken)
    {
        var (schema, item) = await LoadItemAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, itemId, loader, db, cancellationToken);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        var level = schema!.Access.Level(item.ScopeId);
        var grants = level == WorkspaceAccessLevel.Manage ? await ScopeGrantsAsync(db, caller.TenantId, item.ScopeId, cancellationToken) : null;
        var (source, sourceId) = item.HasUniquePermissions ? ((string?)null, (Guid?)null)
            : item.ScopeId != schema.List.Id ? ("item", item.ScopeId)
            : schema.List.HasUniquePermissions ? ("list", schema.List.Id)
            : ("workspace", schema.List.WorkspaceId);
        return TypedResults.Ok(new PermissionsResponse(item.HasUniquePermissions, source, sourceId, level, grants));
    }

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> BreakItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, BreakInheritanceRequest? request, Caller caller, ListSchemaLoader loader, ListsDbContext db,
        ScopeMover mover, IOutbox outbox, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        var (schema, item) = await LoadItemAsync(listCaller, workspaceId, listId, itemId, loader, db, cancellationToken);
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

        // The new scope's entries (a copy of the inherited ones) are saved with the item, before its subtree moves to it.
        var oldScope = item.ScopeId;
        List<PermissionGrantDto> grants = request?.CopyGrants != false ? await ScopeGrantsAsync(db, caller.TenantId, oldScope, cancellationToken) : [];
        db.AclEntries.AddRange(Acl.FromGrants(schema.List, item.Id, WithManager(grants, listCaller, schema.Access)));
        item.HasUniquePermissions = true;
        item.ScopeId = item.Id;
        await mover.SaveScopeChangeAsync(outbox, item, oldScope, cancellationToken);
        return TypedResults.Ok(new PermissionsResponse(true, null, null, WorkspaceAccessLevel.Manage, await ScopeGrantsAsync(db, caller.TenantId, item.Id, cancellationToken)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, ListSchemaLoader loader, ListsDbContext db, ScopeMover mover, IOutbox outbox,
        CancellationToken cancellationToken)
    {
        var (schema, item) = await LoadItemAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, itemId, loader, db, cancellationToken);
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

        var parentScope = item.ParentId is { } parentId ? await mover.ScopeOfAsync(caller.TenantId, parentId, cancellationToken) ?? listId : listId;

        // The scope first gets the parent's entries, so items that have not moved back yet have the parent's access;
        // its entries are deleted once no item uses it.
        var inherited = await ScopeGrantsAsync(db, caller.TenantId, parentScope, cancellationToken);
        Acl.Replace(db, await TrackedEntriesAsync(db, caller.TenantId, itemId, cancellationToken), Acl.FromGrants(schema.List, itemId, inherited));
        item.HasUniquePermissions = false;
        item.ScopeId = parentScope;
        await mover.SaveScopeChangeAsync(outbox, item, itemId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PermissionsResponse>, ValidationProblem, ProblemHttpResult>> ReplaceItemGrantsAsync(
        Guid workspaceId, Guid listId, Guid itemId, ReplaceGrantsRequest request, Caller caller, ListSchemaLoader loader, ListsDbContext db,
        IUserDirectory users, CancellationToken cancellationToken)
    {
        var (schema, item) = await LoadItemAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, itemId, loader, db, cancellationToken);
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

        if (await ReplaceAsync(db, users, schema.List, itemId, request, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(new PermissionsResponse(true, null, null, level, await ScopeGrantsAsync(db, caller.TenantId, item.Id, cancellationToken)));
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>A visible item or folder (tracked), or null (404).</summary>
    private static async Task<(ListSchema? Schema, ListItem? Item)> LoadItemAsync(
        ListCaller caller, Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return (null, null);
        }

        var item = await ItemEndpoints.FindTrackedAsync(db, caller.TenantId, listId, itemId, cancellationToken);
        return item is null || schema.Access.Level(item.ScopeId) < WorkspaceAccessLevel.Read ? (schema, null) : (schema, item);
    }

    internal static Task<List<AclEntry>> TrackedEntriesAsync(ListsDbContext database, Guid tenantId, Guid scopeId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var scope = scopeId;
        var ct = cancellationToken;
        return context.AclEntries.Where(e => e.TenantId == tenant && e.ScopeId == scope).ToListAsync(ct);
    }

    /// <summary>The entries of a scope, as the API shows them.</summary>
    private static async Task<List<PermissionGrantDto>> ScopeGrantsAsync(ListsDbContext database, Guid tenantId, Guid scopeId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var scope = scopeId;
        var ct = cancellationToken;
        var entries = await context.AclEntries.AsNoTracking().Where(e => e.TenantId == tenant && e.ScopeId == scope).ToListAsync(ct);
        return [.. entries.OrderBy(e => e.PrincipalType, StringComparer.Ordinal).ThenBy(e => e.PrincipalId).Select(Acl.ToDto)];
    }

    /// <summary>Makes sure the user breaking inheritance keeps managing the object (unless they have full control anyway).</summary>
    private static List<PermissionGrantDto> WithManager(List<PermissionGrantDto> grants, ListCaller caller, ListAccess access)
    {
        if (access.FullControl || caller.UserId is not { } userId)
        {
            return grants;
        }

        return [.. grants.Where(g => !(g.PrincipalType == AclPrincipalTypes.User && g.PrincipalId == userId)), new PermissionGrantDto(AclPrincipalTypes.User, userId, WorkspaceAccessLevel.Manage)];
    }

    /// <summary>
    /// Checks the grants of a request: known users and groups, workspace roles of the list's workspace, one entry per
    /// principal, and owners only with Manage (they are added when missing).
    /// </summary>
    internal static async Task<Dictionary<string, string[]>?> ValidateAsync(
        IUserDirectory users, ListDefinition list, IReadOnlyList<PermissionGrantDto> grants, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var grant in grants)
        {
            if (!AclPrincipalTypes.IsValid(grant.PrincipalType))
            {
                errors.Add($"{grant.PrincipalId}: principalType must be user, group, workspaceVisitors, workspaceMembers or workspaceOwners.");
            }
            else if (grant.Level is not (WorkspaceAccessLevel.Read or WorkspaceAccessLevel.Contribute or WorkspaceAccessLevel.Manage))
            {
                errors.Add($"{grant.PrincipalId}: level must be read, contribute or manage.");
            }
            else if (Acl.RoleOf(grant.PrincipalType) is not null)
            {
                if (grant.PrincipalId != list.WorkspaceId)
                {
                    errors.Add($"{grant.PrincipalId}: a workspace role needs the id of the list's workspace.");
                }
                else if (grant.PrincipalType == AclPrincipalTypes.WorkspaceOwners && grant.Level != WorkspaceAccessLevel.Manage)
                {
                    errors.Add("Workspace owners always have manage.");
                }
            }
            else if (grant.PrincipalType == AclPrincipalTypes.User
                ? !await users.IsActiveAsync(list.TenantId, grant.PrincipalId, cancellationToken)
                : !await users.GroupExistsAsync(list.TenantId, grant.PrincipalId, cancellationToken))
            {
                errors.Add($"{grant.PrincipalId}: unknown {grant.PrincipalType}.");
            }
        }

        if (grants.GroupBy(g => (g.PrincipalType, g.PrincipalId)).Any(g => g.Count() > 1))
        {
            errors.Add("Each principal can appear once.");
        }

        return errors.Count > 0 ? new Dictionary<string, string[]> { ["grants"] = [.. errors] } : null;
    }

    /// <summary>Replaces the entries of a scope: nothing else is written (ADR-0035).</summary>
    private static async Task<ValidationProblem?> ReplaceAsync(
        ListsDbContext db, IUserDirectory users, ListDefinition list, Guid scopeId, ReplaceGrantsRequest request, CancellationToken cancellationToken)
    {
        var grants = request.Grants ?? [];
        if (await ValidateAsync(users, list, grants, cancellationToken) is { } errors)
        {
            return ApiErrors.Validation(errors);
        }

        Acl.Replace(db, await TrackedEntriesAsync(db, list.TenantId, scopeId, cancellationToken), Acl.FromGrants(list, scopeId, grants));
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }
}

/// <summary>
/// Moves the items inside a folder whose permission scope changed from <see cref="OldScopeId"/> to
/// <see cref="NewScopeId"/>. Saved with the folder change; finishes what the request did not move (large folders, or
/// an interrupted request). Does nothing when the folder has changed again since.
/// </summary>
public sealed record CompleteFolderScopeChange(Guid TenantId, Guid ListId, Guid FolderId, Guid OldScopeId, Guid NewScopeId);

/// <summary>
/// Wolverine handler of <see cref="CompleteFolderScopeChange"/> (generated ahead of time): completes the move, then gives
/// the search documents of the moved items their new scope.
/// </summary>
public static class FolderScopeSubscriber
{
    public static async Task Handle(CompleteFolderScopeChange message, ScopeMover mover, ItemSearchDocuments search, CancellationToken cancellationToken)
    {
        if (await mover.CompleteAsync(message, cancellationToken))
        {
            await search.RefreshScopesAsync(message.TenantId, message.ListId, message.NewScopeId, cancellationToken);
        }
    }
}

/// <summary>
/// Moves the items inside a folder to another permission scope (ADR-0035), folder by folder: one statement moves the
/// inheriting children of a folder. A request moves up to <see cref="ListsOptions.ScopeMoveInlineLimit"/> items;
/// <see cref="CompleteFolderScopeChange"/> (saved with the folder change) moves the rest in the background.
/// </summary>
public sealed class ScopeMover
{
    private const int MaxDepth = 64;
    private readonly ListsDbContext _db;
    private readonly IItemQueries _queries;
    private readonly IOptions<ListsOptions> _options;

    /// <summary>Registered with a factory: the constructor takes module-internal services.</summary>
    internal ScopeMover(ListsDbContext db, IItemQueries queries, IOptions<ListsOptions> options)
    {
        _db = db;
        _queries = queries;
        _options = options;
    }

    /// <summary>
    /// Saves an item whose scope changed from <paramref name="oldScope"/>; for a folder, with the message that completes
    /// the move of its contents, then moves what the request's share allows.
    /// </summary>
    internal async Task SaveScopeChangeAsync(IOutbox outbox, ListItem item, Guid oldScope, CancellationToken cancellationToken)
    {
        // The message also moves the item's search document (and those of a folder's contents) to the new scope.
        var message = new CompleteFolderScopeChange(item.TenantId, item.ListId, item.Id, oldScope, item.ScopeId);
        await outbox.SaveChangesAsync(_db, [], [message], cancellationToken);
        await MoveAndCleanUpAsync(message, inline: true, cancellationToken);
    }

    /// <summary>The scope of an item, also in the recycle bin; null when it does not exist.</summary>
    internal async Task<Guid?> ScopeOfAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var context = _db;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        return await context.Items.AsNoTracking().Where(i => i.TenantId == tenant && i.Id == id).Select(i => (Guid?)i.ScopeId).FirstOrDefaultAsync(ct);
    }

    /// <summary>Completes the move; false when the item has changed scope again since (a later message handles it).</summary>
    internal async Task<bool> CompleteAsync(CompleteFolderScopeChange message, CancellationToken cancellationToken)
    {
        if (await ScopeOfAsync(message.TenantId, message.FolderId, cancellationToken) != message.NewScopeId)
        {
            return false;
        }

        await MoveAndCleanUpAsync(message, inline: false, cancellationToken);
        return true;
    }

    private async Task MoveAndCleanUpAsync(CompleteFolderScopeChange change, bool inline, CancellationToken cancellationToken)
    {
        if (await MoveAsync(change.TenantId, change.FolderId, change.OldScopeId, change.NewScopeId, inline, cancellationToken))
        {
            await RemoveIfUnusedAsync(change.TenantId, change.ListId, change.OldScopeId, cancellationToken);
        }
    }

    /// <summary>
    /// Moves the items below <paramref name="folderId"/> that inherit <paramref name="oldScope"/> to
    /// <paramref name="newScope"/>; items with unique permissions stop the walk, items in the recycle bin move too.
    /// With <paramref name="inline"/> it stops after the request's share. Returns true when nothing is left.
    /// </summary>
    internal async Task<bool> MoveAsync(Guid tenantId, Guid folderId, Guid oldScope, Guid newScope, bool inline, CancellationToken cancellationToken)
    {
        var limit = inline ? _options.Value.ScopeMoveInlineLimit : int.MaxValue;
        var moved = 0;
        List<Guid> frontier = [folderId];
        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var next = new List<Guid>();
            foreach (var parent in frontier)
            {
                if (moved >= limit)
                {
                    return false;
                }

                // Folders that already moved are walked through too, so an interrupted move can resume.
                next.AddRange(await SubfoldersAsync(tenantId, parent, oldScope, newScope, cancellationToken));
                moved += await MoveChildrenAsync(tenantId, parent, oldScope, newScope, cancellationToken);
            }

            frontier = next;
        }

        return true;
    }

    /// <summary>Deletes the access list of a scope no item uses any more (after inheritance was reset or a move).</summary>
    internal async Task RemoveIfUnusedAsync(Guid tenantId, Guid listId, Guid scopeId, CancellationToken cancellationToken)
    {
        var context = _db;
        var tenant = tenantId;
        var list = listId;
        var scope = scopeId;
        var ct = cancellationToken;
        if (scope == list
            || await context.Items.AnyAsync(i => i.TenantId == tenant && i.Id == scope && i.HasUniquePermissions, ct)
            || await context.Items.AnyAsync(i => i.TenantId == tenant && i.ListId == list && i.ScopeId == scope, ct))
        {
            return;
        }

        // The background completion of the same move may remove them at the same time: then the rest is tried again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var entries = await context.AclEntries.Where(e => e.TenantId == tenant && e.ScopeId == scope).ToListAsync(ct);
            if (entries.Count == 0)
            {
                return;
            }

            context.AclEntries.RemoveRange(entries);
            try
            {
                await context.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                foreach (var entry in entries)
                {
                    context.Entry(entry).State = EntityState.Detached;
                }
            }
        }
    }

    private Task<List<Guid>> SubfoldersAsync(Guid tenantId, Guid parentId, Guid oldScope, Guid newScope, CancellationToken cancellationToken)
    {
        var context = _db;
        var tenant = tenantId;
        var parent = parentId;
        var from = oldScope;
        var to = newScope;
        var ct = cancellationToken;
        return context.Items.AsNoTracking()
            .Where(i => i.TenantId == tenant && i.ParentId == parent && i.IsFolder && !i.HasUniquePermissions && (i.ScopeId == from || i.ScopeId == to))
            .Select(i => i.Id)
            .ToListAsync(ct);
    }

    /// <summary>Moves the inheriting children of one folder in one statement (not their versions or ETags: nothing else changed).</summary>
    private Task<int> MoveChildrenAsync(Guid tenantId, Guid parentId, Guid oldScope, Guid newScope, CancellationToken cancellationToken) =>
        _queries.MoveScopeAsync(tenantId, parentId, oldScope, newScope, cancellationToken);
}
