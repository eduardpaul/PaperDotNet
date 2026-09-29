using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        ICurrentUser user, CancellationToken ct)
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
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new PermissionsResponse(true, null, null, WorkspaceAccessLevel.Manage, await ScopeGrantsAsync(db, listId, ct)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetListAsync(
        Guid workspaceId, Guid listId, ListSchemaLoader loader, ListsDbContext db, CancellationToken ct)
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
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PermissionsResponse>, ValidationProblem, ProblemHttpResult>> ReplaceListGrantsAsync(
        Guid workspaceId, Guid listId, ReplaceGrantsRequest request, ListSchemaLoader loader, ListsDbContext db,
        IUserDirectory users, CancellationToken ct)
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

        if (await ReplaceAsync(db, users, schema.List, listId, request, ct) is { } invalid)
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
        ScopeMover mover, ICurrentUser user, IOutbox outbox, ITenantContext tenant, CancellationToken ct)
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

        // The new scope's entries (a copy of the inherited ones) are saved with the item, before its subtree moves to it.
        var oldScope = item.ScopeId;
        var grants = request?.CopyGrants != false ? await ScopeGrantsAsync(db, oldScope, ct) : [];
        db.AclEntries.AddRange(Acl.FromGrants(schema.List, item.Id, WithManager(grants, user, schema.Access)));
        item.HasUniquePermissions = true;
        item.ScopeId = item.Id;
        await SaveScopeChangeAsync(db, outbox, mover, tenant, user, schema.List, item, oldScope, ct);
        if (item.IsFolder)
        {
            await mover.MoveAsync(listId, item.Id, oldScope, item.Id, inline: true, ct);
        }

        return TypedResults.Ok(new PermissionsResponse(true, null, null, WorkspaceAccessLevel.Manage, await ScopeGrantsAsync(db, item.Id, ct)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetItemAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, ScopeMover mover, IOutbox outbox,
        ITenantContext tenant, ICurrentUser user, CancellationToken ct)
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

        // The scope first gets the parent's entries, so items that have not moved back yet have the parent's access;
        // its entries are deleted once no item uses it.
        var inherited = await ScopeGrantsAsync(db, parentScope, ct);
        Acl.Replace(db, await db.AclEntries.Where(e => e.ScopeId == itemId).ToListAsync(ct), Acl.FromGrants(schema.List, itemId, inherited));
        item.HasUniquePermissions = false;
        item.ScopeId = parentScope;
        await SaveScopeChangeAsync(db, outbox, mover, tenant, user, schema.List, item, itemId, ct);
        if (!item.IsFolder || await mover.MoveAsync(listId, item.Id, itemId, parentScope, inline: true, ct))
        {
            await mover.RemoveIfUnusedAsync(listId, itemId, ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PermissionsResponse>, ValidationProblem, ProblemHttpResult>> ReplaceItemGrantsAsync(
        Guid workspaceId, Guid listId, Guid itemId, ReplaceGrantsRequest request, ListSchemaLoader loader, ListsDbContext db,
        IUserDirectory users, CancellationToken ct)
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

        if (await ReplaceAsync(db, users, schema.List, itemId, request, ct) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(new PermissionsResponse(true, null, null, level, await ScopeGrantsAsync(db, item.Id, ct)));
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>
    /// Saves an item that moved to another scope: a document's search entry follows it, and a folder's contents are
    /// completed in the background if the request does not move them all.
    /// </summary>
    private static Task SaveScopeChangeAsync(
        ListsDbContext db, IOutbox outbox, ScopeMover mover, ITenantContext tenant, ICurrentUser user, ListDefinition list, ListItem item,
        Guid oldScope, CancellationToken ct) =>
        outbox.SaveChangesAsync(
            db,
            item.IsFolder ? [] : [mover.Changed(list.Id, [item.Id])],
            item.IsFolder ? [new CompleteFolderScopeChange(list.Id, item.Id, oldScope, item.ScopeId, tenant.TenantId!.Value, tenant.TenantIdentifier!, user.UserId)] : null,
            ct);

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

    /// <summary>Replaces the entries of a scope: nothing else is written, not even the search index (ADR-0035).</summary>
    private static async Task<ValidationProblem?> ReplaceAsync(
        ListsDbContext db, IUserDirectory users, ListDefinition list, Guid scopeId, ReplaceGrantsRequest request, CancellationToken ct)
    {
        var grants = request.Grants ?? [];
        if (await ValidateAsync(users, list, grants, ct) is { } errors)
        {
            return ApiErrors.Validation(errors);
        }

        Acl.Replace(db, await db.AclEntries.Where(e => e.ScopeId == scopeId).ToListAsync(ct), Acl.FromGrants(list, scopeId, grants));
        await db.SaveChangesAsync(ct);
        return null;
    }
}

/// <summary>Search documents of these items take their items' current permission scope (ADR-0035).</summary>
public sealed record ItemScopesChanged : IntegrationEvent
{
    public required Guid ListId { get; init; }

    public required IReadOnlyList<Guid> ItemIds { get; init; }
}

/// <summary>
/// Moves the items inside a folder to another permission scope (ADR-0035): level by level, in chunks of
/// <see cref="ChunkSize"/>. Each chunk updates the items, logs them for delta with the scope they came from, and
/// publishes <see cref="ItemScopesChanged"/> for search, in one transaction. A request moves up to
/// <see cref="ListsOptions.ScopeMoveInlineLimit"/> items; <see cref="CompleteFolderScopeChange"/> (saved with the
/// folder change) moves the rest in the background.
/// </summary>
internal sealed class ScopeMover(
    ListsDbContext db, IOutbox outbox, ITenantContext tenant, ICurrentUser user, EventCausation causation, TimeProvider time,
    IOptions<ListsOptions> options)
{
    public const int ChunkSize = 2_000;
    private const int MaxDepth = 64;

    /// <summary>
    /// Moves the items below <paramref name="folderId"/> that inherit <paramref name="oldScope"/> to
    /// <paramref name="newScope"/>; folders with unique permissions stop the walk, items in the recycle bin move too.
    /// With <paramref name="inline"/> it stops after the request's share. Returns true when nothing is left.
    /// </summary>
    public async Task<bool> MoveAsync(Guid listId, Guid folderId, Guid oldScope, Guid newScope, bool inline, CancellationToken ct)
    {
        var limit = inline ? options.Value.ScopeMoveInlineLimit : int.MaxValue;
        var moved = 0;
        var all = db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]);
        List<Guid> frontier = [folderId];
        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var next = new List<Guid>();
            foreach (var parents in frontier.Chunk(ChunkSize))
            {
                // Items that already moved are walked through too, so an interrupted move can resume.
                var children = await all
                    .Where(i => i.ParentId != null && EF.Parameter(parents).Contains(i.ParentId.Value) && !i.HasUniquePermissions
                                && (i.ScopeId == oldScope || i.ScopeId == newScope))
                    .Select(i => new { i.Id, i.IsFolder, i.ScopeId, Deleted = i.DeletedAt != null })
                    .ToListAsync(ct);
                next.AddRange(children.Where(c => c.IsFolder).Select(c => c.Id));
                foreach (var chunk in children.Where(c => c.ScopeId == oldScope).Chunk(ChunkSize))
                {
                    if (moved >= limit)
                    {
                        return false;
                    }

                    // Items in the recycle bin move too, but delta has nothing to tell about them.
                    moved += await MoveChunkAsync(
                        listId, [.. chunk.Select(c => c.Id)], [.. chunk.Where(c => !c.Deleted).Select(c => c.Id)], oldScope, newScope, ct);
                }
            }

            frontier = next;
        }

        return true;
    }

    /// <summary>Deletes the access list of a scope no item uses any more (after inheritance was reset).</summary>
    public async Task RemoveIfUnusedAsync(Guid listId, Guid scopeId, CancellationToken ct)
    {
        var all = db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]);
        if (scopeId == listId
            || await all.AnyAsync(i => i.Id == scopeId && i.HasUniquePermissions, ct)
            || await all.AnyAsync(i => i.ListId == listId && i.ScopeId == scopeId, ct))
        {
            return;
        }

        await db.AclEntries.Where(e => e.ScopeId == scopeId).ExecuteDeleteAsync(ct);
    }

    private async Task<int> MoveChunkAsync(Guid listId, Guid[] ids, Guid[] active, Guid oldScope, Guid newScope, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var count = await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(i => EF.Parameter(ids).Contains(i.Id) && i.ScopeId == oldScope)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ScopeId, newScope), ct);
        if (count == 0)
        {
            // Moved already by the request or by the background completion running at the same time.
            return 0;
        }

        db.ItemChanges.AddRange(active.Select(id => new ItemChange
        {
            ListId = listId,
            ItemId = id,
            ScopeId = newScope,
            FromScopeId = oldScope,
            Kind = ItemChangeKind.Upserted,
            At = now,
        }));
        await outbox.SaveChangesAsync(db, [Changed(listId, ids)], cancellationToken: ct);

        // The outbox may already have committed the transaction with its messages.
        if (db.Database.CurrentTransaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return count;
    }

    public ItemScopesChanged Changed(Guid listId, IReadOnlyList<Guid> itemIds) => new()
    {
        TenantId = tenant.TenantId!.Value,
        TenantIdentifier = tenant.TenantIdentifier!,
        UserId = user.UserId,
        Depth = causation.Depth,
        ListId = listId,
        ItemIds = itemIds,
    };
}

/// <summary>
/// Moves the items inside a folder whose permission scope changed from <see cref="OldScopeId"/> to
/// <see cref="NewScopeId"/>. Saved with the folder change; finishes what the request did not move (large folders, or
/// an interrupted request). Does nothing when the folder has changed again since.
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

        var mover = scope.ServiceProvider.GetRequiredService<ScopeMover>();
        await mover.MoveAsync(message.ListId, message.FolderId, message.OldScopeId, message.NewScopeId, inline: false, cancellationToken);
        await mover.RemoveIfUnusedAsync(message.ListId, message.OldScopeId, cancellationToken);
    }
}
