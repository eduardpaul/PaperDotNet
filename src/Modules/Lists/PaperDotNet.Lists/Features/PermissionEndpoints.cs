using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// A permission entry. For workspace roles (<c>workspaceVisitors</c>, <c>workspaceMembers</c>, <c>workspaceOwners</c>)
/// the principal id is the workspace id.
/// </summary>
public sealed record PermissionGrantDto(AclPrincipalType PrincipalType, [property: Required] Guid PrincipalId, WorkspaceAccessLevel Level);

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
/// Permission inheritance (IAM-07, ADR-0035): workspace roles → list → folder → item. A list inherits through entries
/// for the workspace roles; breaking inheritance copies the inherited entries (by default), resetting returns to the
/// parent's. Workspace owners always keep Manage.
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
        Guid workspaceId, Guid listId, ListSchemaLoader loader, ListsDbContext db, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        var list = schema.List;
        var grants = schema.Permission == WorkspaceAccessLevel.Manage ? await ScopeGrantsAsync(db, list.Id, ct) : null;
        return TypedResults.Ok(new PermissionsResponse(
            list.HasUniquePermissions, list.HasUniquePermissions ? null : "workspace", list.HasUniquePermissions ? null : list.WorkspaceId, schema.Permission, grants));
    }

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> BreakListAsync(
        Guid workspaceId, Guid listId, BreakInheritanceRequest? request, ListSchemaLoader loader, ListsDbContext db,
        ICurrentUser user, IOutbox outbox, ITenantContext tenant, CancellationToken ct)
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

        // The list's entries stay as they are (a copy of the inherited ones) unless they should start empty.
        var existing = await db.AclEntries.Where(e => e.ScopeId == listId).ToListAsync(ct);
        var grants = request?.CopyGrants != false ? existing.Select(Acl.ToDto).ToList() : [];
        Acl.Replace(db, existing, Acl.FromGrants(schema.List, listId, WithManager(grants, user, schema.Access)));
        schema.List.HasUniquePermissions = true;
        await outbox.SaveChangesAsync(db, [ListIndexInvalidated.For(tenant, user, listId)], cancellationToken: ct);
        return TypedResults.Ok(new PermissionsResponse(true, null, null, WorkspaceAccessLevel.Manage, await ScopeGrantsAsync(db, listId, ct)));
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
            Acl.Replace(db, await db.AclEntries.Where(e => e.ScopeId == listId).ToListAsync(ct), Acl.RoleEntries(schema.List));
            schema.List.HasUniquePermissions = false;
            await outbox.SaveChangesAsync(db, [ListIndexInvalidated.For(tenant, user, listId)], cancellationToken: ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PermissionsResponse>, ValidationProblem, ProblemHttpResult>> ReplaceListGrantsAsync(
        Guid workspaceId, Guid listId, ReplaceGrantsRequest request, ListSchemaLoader loader, ListsDbContext db,
        IUserDirectory users, IOutbox outbox, ITenantContext tenant, ICurrentUser user, CancellationToken ct)
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

        if (await ReplaceAsync(db, users, schema.List, listId, request, ListIndexInvalidated.For(tenant, user, listId), outbox, ct) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(new PermissionsResponse(true, null, null, schema.Permission, await ScopeGrantsAsync(db, listId, ct)));
    }

    // ---- Items --------------------------------------------------------------

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> GetItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, CancellationToken ct)
    {
        var (schema, item) = await LoadItemAsync(workspaceId, listId, itemId, loader, db, tracking: false, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        var level = schema!.Access.Level(item.ScopeId);
        var grants = level == WorkspaceAccessLevel.Manage ? await ScopeGrantsAsync(db, item.ScopeId, ct) : null;
        var (source, sourceId) = item.HasUniquePermissions ? ((string?)null, (Guid?)null)
            : item.ScopeId != schema.List.Id ? ("item", item.ScopeId)
            : schema.List.HasUniquePermissions ? ("list", schema.List.Id)
            : ("workspace", schema.List.WorkspaceId);
        return TypedResults.Ok(new PermissionsResponse(item.HasUniquePermissions, source, sourceId, level, grants));
    }

    private static async Task<Results<Ok<PermissionsResponse>, ProblemHttpResult>> BreakItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, BreakInheritanceRequest? request, ListSchemaLoader loader, ListsDbContext db,
        ICurrentUser user, IOutbox outbox, ITenantContext tenant, CancellationToken ct)
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

        // The new scope's entries are written with the item, before its subtree moves to it.
        var oldScope = item.ScopeId;
        var grants = request?.CopyGrants != false ? await ScopeGrantsAsync(db, oldScope, ct) : [];
        db.AclEntries.AddRange(Acl.FromGrants(schema.List, item.Id, WithManager(grants, user, schema.Access)));
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

        return TypedResults.Ok(new PermissionsResponse(true, null, null, WorkspaceAccessLevel.Manage, await ScopeGrantsAsync(db, item.Id, ct)));
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
            ? await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).Where(i => i.Id == parentId).Select(i => (Guid?)i.ScopeId).FirstOrDefaultAsync(ct) ?? listId
            : listId;
        db.AclEntries.RemoveRange(await db.AclEntries.Where(e => e.ScopeId == itemId).ToListAsync(ct));
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
        IUserDirectory users, IOutbox outbox, ITenantContext tenant, ICurrentUser user, CancellationToken ct)
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

        if (await ReplaceAsync(db, users, schema.List, itemId, request, ListIndexInvalidated.For(tenant, user, listId), outbox, ct) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(new PermissionsResponse(true, null, null, level, await ScopeGrantsAsync(db, item.Id, ct)));
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

    /// <summary>The entries of a scope, as the API shows them.</summary>
    private static async Task<List<PermissionGrantDto>> ScopeGrantsAsync(ListsDbContext db, Guid scopeId, CancellationToken ct) =>
        (await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == scopeId).ToListAsync(ct))
            .OrderBy(e => e.PrincipalType).ThenBy(e => e.PrincipalId)
            .Select(Acl.ToDto)
            .ToList();

    /// <summary>Makes sure the user breaking inheritance keeps managing the object (unless they have full control anyway).</summary>
    private static List<PermissionGrantDto> WithManager(List<PermissionGrantDto> grants, ICurrentUser user, ListAccess access)
    {
        if (access.FullControl || user.UserId is not { } userId)
        {
            return grants;
        }

        return [.. grants.Where(g => !(g.PrincipalType == AclPrincipalType.User && g.PrincipalId == userId)), new PermissionGrantDto(AclPrincipalType.User, userId, WorkspaceAccessLevel.Manage)];
    }

    /// <summary>
    /// Checks the grants of a request: known users and groups, workspace roles of the list's workspace, one entry per
    /// principal, and owners only with Manage (they are added when missing).
    /// </summary>
    internal static async Task<Dictionary<string, string[]>?> ValidateAsync(
        IUserDirectory users, ListDefinition list, IReadOnlyList<PermissionGrantDto> grants, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var grant in grants)
        {
            if (grant.Level is not (WorkspaceAccessLevel.Read or WorkspaceAccessLevel.Contribute or WorkspaceAccessLevel.Manage))
            {
                errors.Add($"{grant.PrincipalId}: level must be read, contribute or manage.");
            }
            else if (Acl.RoleOf(grant.PrincipalType) is not null)
            {
                if (grant.PrincipalId != list.WorkspaceId)
                {
                    errors.Add($"{grant.PrincipalId}: a workspace role needs the id of the list's workspace.");
                }
                else if (grant.PrincipalType == AclPrincipalType.WorkspaceOwners && grant.Level != WorkspaceAccessLevel.Manage)
                {
                    errors.Add("Workspace owners always have manage.");
                }
            }
            else if (grant.PrincipalType == AclPrincipalType.User ? !await users.IsActiveAsync(grant.PrincipalId, ct) : !await users.GroupExistsAsync(grant.PrincipalId, ct))
            {
                errors.Add($"{grant.PrincipalId}: unknown {(grant.PrincipalType == AclPrincipalType.User ? "user" : "group")}.");
            }
        }

        if (grants.GroupBy(g => (g.PrincipalType, g.PrincipalId)).Any(g => g.Count() > 1))
        {
            errors.Add("Each principal can appear once.");
        }

        return errors.Count > 0 ? new Dictionary<string, string[]> { ["grants"] = [.. errors] } : null;
    }

    private static async Task<ValidationProblem?> ReplaceAsync(
        ListsDbContext db, IUserDirectory users, ListDefinition list, Guid scopeId, ReplaceGrantsRequest request,
        ListIndexInvalidated invalidated, IOutbox outbox, CancellationToken ct)
    {
        var grants = request.Grants ?? [];
        if (await ValidateAsync(users, list, grants, ct) is { } errors)
        {
            return ApiErrors.Validation(errors);
        }

        Acl.Replace(db, await db.AclEntries.Where(e => e.ScopeId == scopeId).ToListAsync(ct), Acl.FromGrants(list, scopeId, grants));
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
    /// Includes items in the recycle bin. Returns the number of items changed (0 when it already happened).
    /// </summary>
    public static async Task<int> ReassignAsync(ListsDbContext db, Guid folderId, Guid oldScope, Guid newScope, CancellationToken ct)
    {
        var changed = 0;
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
                changed += await all.Where(i => ids.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.ScopeId, newScope), ct);
            }

            frontier = children.Where(c => c.IsFolder).Select(c => c.Id).ToList();
        }

        return changed;
    }
}

/// <summary>
/// Reassigns the items inside a folder whose permission scope changed from <see cref="OldScopeId"/> to
/// <see cref="NewScopeId"/>. Saved with the folder change; does nothing when the request already did it or the folder
/// has changed again since.
/// </summary>
public sealed record CompleteFolderScopeChange(
    Guid ListId, Guid FolderId, Guid OldScopeId, Guid NewScopeId, Guid TenantId, string TenantIdentifier, Guid? UserId) : ITenantMessage;

/// <summary>Wolverine handler for <see cref="CompleteFolderScopeChange"/> (discovered by convention).</summary>
public static class CompleteFolderScopeChangeHandler
{
    public static async Task Handle(CompleteFolderScopeChange message, ITenantScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateScope(message.TenantId, message.TenantIdentifier, message.UserId);
        var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
        var folder = await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == message.FolderId, cancellationToken);
        if (folder is null || folder.ScopeId != message.NewScopeId)
        {
            return;
        }

        if (await ScopeTree.ReassignAsync(db, message.FolderId, message.OldScopeId, message.NewScopeId, cancellationToken) > 0)
        {
            var services = scope.ServiceProvider;
            await services.GetRequiredService<IOutbox>().SaveChangesAsync(
                db, [ListIndexInvalidated.For(services.GetRequiredService<ITenantContext>(), services.GetRequiredService<ICurrentUser>(), message.ListId)],
                cancellationToken: cancellationToken);
        }
    }
}
