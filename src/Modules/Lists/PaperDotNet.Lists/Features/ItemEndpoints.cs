using System.Text.Json;
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

/// <summary>Create body: <c>{ "contentTypeId"?, "parentId"?, "isFolder"?, "fields": { "title": …, … } }</c>.</summary>
public sealed record CreateItemRequest(Guid? ContentTypeId, Guid? ParentId, bool IsFolder, JsonElement? Fields);

internal static class ItemEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group($"{ListEndpoints.Route}/{{listId:guid}}/items", "Items");
        group.MapGet("", QueryAsync).RequireScope(ListScopes.Read).WithName("ListItems");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.Write).WithName("CreateItem");
        group.MapGet("/{itemId:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetItem");
        group.MapGet("/{itemId:guid}/children", ChildrenAsync).RequireScope(ListScopes.Read).WithName("ListFolderChildren");
        group.MapPatch("/{itemId:guid}", UpdateAsync).RequireScope(ListScopes.Write).WithName("UpdateItem");
        group.MapDelete("/{itemId:guid}", DeleteAsync).RequireScope(ListScopes.Write).WithName("DeleteItem");
    }

    /// <summary>
    /// Items of the list. Supports <c>$filter</c>, <c>$orderby</c>, <c>$top</c>, <c>$skiptoken</c>,
    /// <c>$count</c>, <c>$select</c> (field names) and <c>viewId</c>.
    /// </summary>
    private static async Task<Results<Ok<ItemPage>, ValidationProblem, ProblemHttpResult>> QueryAsync(
        Guid workspaceId, Guid listId, Guid? viewId, ListSchemaLoader loader, ItemQueryRunner runner, ListsDbContext db,
        HttpRequest http, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        ListView? view = null;
        if (viewId is { } id)
        {
            view = await db.Views.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && v.ListId == listId, ct);
            if (view is null)
            {
                return ApiErrors.NotFound("The view was not found.");
            }
        }

        return await RunAsync(schema, view, null, runner, http, ct);
    }

    private static async Task<Results<Ok<ItemPage>, ValidationProblem, ProblemHttpResult>> ChildrenAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ItemQueryRunner runner, ListsDbContext db,
        HttpRequest http, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null || !await db.Items.AnyAsync(i => i.Id == itemId && i.ListId == listId && i.IsFolder, ct))
        {
            return ApiErrors.NotFound();
        }

        return await RunAsync(schema, null, i => i.ParentId == itemId, runner, http, ct);
    }

    private static async Task<Results<Ok<ItemPage>, ValidationProblem, ProblemHttpResult>> RunAsync(
        ListSchema schema, ListView? view, System.Linq.Expressions.Expression<Func<ListItem, bool>>? scope,
        ItemQueryRunner runner, HttpRequest http, CancellationToken ct)
    {
        var (page, error) = await runner.RunAsync(schema, ItemQueryOptions.From(http), view, scope, http, ct);
        return page is null
            ? ApiErrors.Validation(new Dictionary<string, string[]> { ["query"] = [error!] })
            : TypedResults.Ok(page);
    }

    private static async Task<Results<Ok<ItemResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, HttpResponse response, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        var item = schema is null ? null : await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, item.Version);
        return TypedResults.Ok(ItemResponse.From(item));
    }

    private static async Task<Results<Created<ItemResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, Guid listId, CreateItemRequest request, ListSchemaLoader loader, ItemWriter writer,
        HttpResponse response, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Contribute)
        {
            return ListEndpoints.Forbidden();
        }

        var result = await writer.CreateAsync(schema, request.ContentTypeId, request.ParentId, request.IsFolder, request.Fields, ct);
        if (result.Errors is not null)
        {
            return ApiErrors.Validation(result.Errors);
        }

        if (result.Cancelled is not null)
        {
            return CancelledByReceiver(result.Cancelled);
        }

        ETags.Set(response, result.Item!.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/lists/{listId}/items/{result.Item.Id}", ItemResponse.From(result.Item));
    }

    /// <summary>
    /// PATCH: <c>fields</c> are merged (null removes a value); <c>parentId</c> moves the item
    /// (null = list root); <c>contentTypeId</c> changes its content type. Requires <c>If-Match</c>.
    /// </summary>
    private static async Task<Results<Ok<ItemResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid workspaceId, Guid listId, Guid itemId, JsonElement body, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["body"] = ["A JSON object is expected."] });
        }

        var (schema, item, problem) = await LoadForChangeAsync(workspaceId, listId, itemId, loader, db, http, ct);
        if (problem is not null)
        {
            return problem;
        }

        Guid? contentTypeId = null;
        var parentId = Optional<Guid?>.None;
        JsonElement? fields = null;
        foreach (var property in body.EnumerateObject())
        {
            switch (property.Name)
            {
                case "fields":
                    fields = property.Value;
                    break;
                case "contentTypeId" when property.Value.TryGetGuid(out var ctId):
                    contentTypeId = ctId;
                    break;
                case "parentId" when property.Value.ValueKind == JsonValueKind.Null:
                    parentId = Optional<Guid?>.Of(null);
                    break;
                case "parentId" when property.Value.TryGetGuid(out var pId):
                    parentId = Optional<Guid?>.Of(pId);
                    break;
                default:
                    return ApiErrors.Validation(new Dictionary<string, string[]> { [property.Name] = ["Unknown or invalid property."] });
            }
        }

        try
        {
            var result = await writer.UpdateAsync(schema!, item!, contentTypeId, parentId, fields, ct);
            if (result.Errors is not null)
            {
                return ApiErrors.Validation(result.Errors);
            }

            if (result.Cancelled is not null)
            {
                return CancelledByReceiver(result.Cancelled);
            }

            ETags.Set(response, result.Item!.Version);
            return TypedResults.Ok(ItemResponse.From(result.Item));
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer,
        HttpRequest http, CancellationToken ct)
    {
        var (schema, item, problem) = await LoadForChangeAsync(workspaceId, listId, itemId, loader, db, http, ct);
        if (problem is not null)
        {
            return problem;
        }

        try
        {
            var result = await writer.DeleteAsync(schema!, item!, ct);
            return result switch
            {
                { Conflict: { } conflict } => ApiErrors.Conflict("folderNotEmpty", conflict),
                { Cancelled: { } message } => CancelledByReceiver(message),
                _ => TypedResults.NoContent(),
            };
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }
    }

    internal static ProblemHttpResult CancelledByReceiver(string message) =>
        ApiErrors.Conflict("cancelledByReceiver", message);

    internal static async Task<(ListSchema? Schema, ListItem? Item, ProblemHttpResult? Problem)> LoadForChangeAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, HttpRequest http, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        var item = schema is null ? null : await db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct);
        if (item is null)
        {
            return (null, null, ApiErrors.NotFound());
        }

        if (schema!.Permission < WorkspaceAccessLevel.Contribute)
        {
            return (null, null, ListEndpoints.Forbidden());
        }

        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return (null, null, ApiErrors.PreconditionRequired());
        }

        if (version != item.Version)
        {
            return (null, null, ApiErrors.PreconditionFailed());
        }

        db.Entry(item).Property(i => i.Version).OriginalValue = version;
        return (schema, item, null);
    }
}
