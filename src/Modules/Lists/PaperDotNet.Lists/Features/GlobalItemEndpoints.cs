using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>An item resolved by stable identity, with its current location and the caller's relationship access.</summary>
public sealed record LocatedItemResponse(ItemResponse Item, Guid WorkspaceId, string WorkspaceName, string ListName, string ContentTypeName, bool CanRelate);

public sealed record MoveItemRequest(Guid WorkspaceId, Guid ListId, Guid? ParentId = null);

/// <summary>Location-independent item resolution and symmetric, tenant-local relationships for every content type.</summary>
internal static class GlobalItemEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("items", "Global items");
        group.MapGet("", QueryAsync).RequireScope(ListScopes.Read).WithName("FindGlobalItems").WithQueryOptions(QueryOptions.Paging);
        group.MapGet("/{itemId:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetGlobalItem");
        group.MapGet("/{itemId:guid}/relations", RelationsAsync).RequireScope(ListScopes.Read).WithName("ListItemRelations").WithQueryOptions(QueryOptions.Paging);
        group.MapPut("/{itemId:guid}/relations/{otherId:guid}", AddRelationAsync).RequireScope(ListScopes.Write).WithName("RelateItems");
        group.MapDelete("/{itemId:guid}/relations/{otherId:guid}", RemoveRelationAsync).RequireScope(ListScopes.Write).WithName("UnrelateItems");
        group.MapPost("/{itemId:guid}/move", MoveAsync).RequireScope(ListScopes.Write).WithName("MoveGlobalItem");
    }

    private static async Task<Results<Ok<LocatedItemResponse>, ProblemHttpResult>> GetAsync(
        Guid itemId, ListsDbContext db, ListSchemaLoader loader, IWorkspaceAccess workspaces, HttpResponse response, CancellationToken ct)
    {
        var (schema, item) = await LoadAsync(itemId, db, loader, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, item.Version);
        var names = await workspaces.GetNamesAsync([schema!.List.WorkspaceId], ct);
        return TypedResults.Ok(Located(schema, item, names));
    }

    /// <summary>Finds accessible content items by title (q), optionally restricted to a workspace or writable items.</summary>
    private static async Task<Ok<Page<LocatedItemResponse>>> QueryAsync(
        string? q, Guid? workspaceId, bool? writable, ListsDbContext db, ListSchemaLoader loader, IWorkspaceAccess workspaces,
        HttpRequest http, CancellationToken ct) =>
        TypedResults.Ok(await PageAsync(null, q, workspaceId, writable == true, db, loader, workspaces, http, ct));

    private static async Task<Results<Ok<Page<LocatedItemResponse>>, ProblemHttpResult>> RelationsAsync(
        Guid itemId, ListsDbContext db, ListSchemaLoader loader, IWorkspaceAccess workspaces, HttpRequest http, CancellationToken ct)
    {
        var (_, item) = await LoadAsync(itemId, db, loader, ct);
        return item is null
            ? ApiErrors.NotFound()
            : TypedResults.Ok(await PageAsync(itemId, null, null, false, db, loader, workspaces, http, ct));
    }

    private static async Task<Page<LocatedItemResponse>> PageAsync(
        Guid? relatedTo, string? q, Guid? workspaceId, bool writable, ListsDbContext db, ListSchemaLoader loader,
        IWorkspaceAccess workspaces, HttpRequest http, CancellationToken ct)
    {
        var memberships = await workspaces.GetMyWorkspacesAsync(ct);
        if (workspaceId is { } workspace)
        {
            memberships = memberships.Where(m => m.WorkspaceId == workspace).ToList();
        }

        var visible = await loader.VisibleListsAsync(memberships, ct);
        var schemas = await loader.LoadManyAsync(visible.Select(l => l.Id).ToArray(), system: false, ct);
        var required = writable ? WorkspaceAccessLevel.Contribute : WorkspaceAccessLevel.Read;
        var listIds = schemas.Select(s => s.List.Id).ToArray();
        var fullLists = schemas.Where(s => s.Access.FullControl).Select(s => s.List.Id).ToArray();
        var scopes = schemas.SelectMany(s => s.Access.Scopes(required)).Distinct().ToArray();
        var query = db.Items.AsNoTracking().Where(i => !i.IsFolder && EF.Parameter(listIds).Contains(i.ListId)
            && (EF.Parameter(fullLists).Contains(i.ListId) || EF.Parameter(scopes).Contains(i.ScopeId)));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.Trim().ToLowerInvariant();
            // EF translates these calls to SQL lower/contains; culture/comparison overloads are not translatable.
#pragma warning disable CA1304, CA1311, CA1862
            query = query.Where(i => i.Title.ToLower().Contains(text));
#pragma warning restore CA1304, CA1311, CA1862
        }

        if (relatedTo is { } id)
        {
            query = query.Where(i => db.Relations.Any(r => (r.FirstItemId == id && r.SecondItemId == i.Id)
                || (r.SecondItemId == id && r.FirstItemId == i.Id)));
        }

        var page = PageRequest.From(http);
        var items = await query.Where(i => page.After == null || i.Id.CompareTo(page.After.Value) > 0)
            .OrderBy(i => i.Id).Take(page.Top + 1).ToListAsync(ct);
        var byList = schemas.ToDictionary(s => s.List.Id);
        var names = await workspaces.GetNamesAsync(items.Select(i => byList[i.ListId].List.WorkspaceId).Distinct().ToArray(), ct);
        return Page.Create(items.Select(i => Located(byList[i.ListId], i, names)).ToList(), page, http, r => r.Item.Id);
    }

    private static Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> AddRelationAsync(
        Guid itemId, Guid otherId, ListsDbContext db, ListSchemaLoader loader, ItemWriter writer, CancellationToken ct) =>
        ChangeRelationAsync(itemId, otherId, true, db, loader, writer, ct);

    private static Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> RemoveRelationAsync(
        Guid itemId, Guid otherId, ListsDbContext db, ListSchemaLoader loader, ItemWriter writer, CancellationToken ct) =>
        ChangeRelationAsync(itemId, otherId, false, db, loader, writer, ct);

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> ChangeRelationAsync(
        Guid itemId, Guid otherId, bool add, ListsDbContext db, ListSchemaLoader loader, ItemWriter writer, CancellationToken ct)
    {
        var (sourceSchema, source) = await LoadAsync(itemId, db, loader, ct);
        var (targetSchema, target) = await LoadAsync(otherId, db, loader, ct);
        if (source is null || target is null)
        {
            return ApiErrors.NotFound();
        }

        if (itemId == otherId)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["otherId"] = ["An item cannot relate to itself."] });
        }

        if (sourceSchema!.Access.Level(source.ScopeId) < WorkspaceAccessLevel.Contribute
            || targetSchema!.Access.Level(target.ScopeId) < WorkspaceAccessLevel.Contribute)
        {
            return ListEndpoints.Forbidden();
        }

        return await writer.RelateItemsAsync(sourceSchema, source, targetSchema, target, add, ct)
            ? TypedResults.NoContent()
            : ApiErrors.PreconditionFailed();
    }

    private static async Task<Results<Ok<LocatedItemResponse>, ValidationProblem, ProblemHttpResult>> MoveAsync(
        Guid itemId, MoveItemRequest request, ListsDbContext db, ListSchemaLoader loader, ItemWriter writer,
        IWorkspaceAccess workspaces, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        var (schema, item) = await LoadAsync(itemId, db, loader, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        var (_, _, problem) = await ItemEndpoints.LoadForChangeAsync(schema!.List.WorkspaceId, item.ListId, item.Id, loader, db, http, ct);
        if (problem is not null)
        {
            return problem;
        }

        var destination = await loader.LoadAsync(request.WorkspaceId, request.ListId, ct);
        if (destination is null)
        {
            return ApiErrors.NotFound();
        }

        try
        {
            var result = await writer.MoveAcrossListsAsync(schema, destination, item, request.ParentId, ct);
            if (result.Forbidden)
            {
                return ListEndpoints.Forbidden();
            }

            if (result.Errors is not null)
            {
                return ApiErrors.Validation(result.Errors);
            }

            if (result.Cancelled is not null)
            {
                return ItemEndpoints.CancelledByMutator(result.Cancelled);
            }

            ETags.Set(response, result.Item!.Version);
            var names = await workspaces.GetNamesAsync([destination.List.WorkspaceId], ct);
            return TypedResults.Ok(Located(destination, result.Item, names));
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }
    }

    private static LocatedItemResponse Located(ListSchema schema, ListItem item, IReadOnlyDictionary<Guid, string> names) =>
        new(ItemResponse.From(item), schema.List.WorkspaceId, names.GetValueOrDefault(schema.List.WorkspaceId) ?? "",
            schema.List.Name, schema.FindContentType(item.ContentTypeId)?.Name ?? "Item",
            schema.Access.Level(item.ScopeId) >= WorkspaceAccessLevel.Contribute);

    private static async Task<(ListSchema? Schema, ListItem? Item)> LoadAsync(Guid id, ListsDbContext db, ListSchemaLoader loader, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id && !i.IsFolder, ct);
        var list = item is null ? null : await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == item.ListId, ct);
        var schema = list is null ? null : await loader.LoadAsync(list.WorkspaceId, list.Id, ct);
        return schema is not null && schema.Access.Level(item!.ScopeId) >= WorkspaceAccessLevel.Read ? (schema, item) : (null, null);
    }
}
