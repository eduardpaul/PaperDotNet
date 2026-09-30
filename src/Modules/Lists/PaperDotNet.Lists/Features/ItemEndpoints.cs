using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// Create body: <c>{ "id"?, "contentTypeId"?, "parentId"?, "isFolder"?, "fields": { "title": …, … } }</c>. With
/// <c>id</c> the client chooses the item's id, so repeating a create is safe (the item it made is returned unchanged, 200).
/// </summary>
public sealed record CreateItemRequest(Guid? ContentTypeId, Guid? ParentId, bool IsFolder, JsonObject? Fields, Guid? Id = null);

/// <summary>A list item as returned by the API. <c>fields</c> contains <c>title</c> and all field values.</summary>
public sealed record ItemResponse(
    Guid Id,
    Guid ListId,
    Guid ContentTypeId,
    Guid? ParentId,
    bool IsFolder,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedBy,
    JsonObject Fields,
    [property: JsonPropertyName("@odata.etag")] string ETag)
{
    internal static ItemResponse From(ListItem item, IReadOnlyList<string>? select = null)
    {
        var fields = ItemWriter.Values(item);
        var ordered = new JsonObject { ["title"] = item.Title };
        foreach (var (key, value) in fields.Where(p => p.Key != "title").ToList())
        {
            fields.Remove(key);
            ordered[key] = value;
        }

        if (select is not null)
        {
            foreach (var key in ordered.Select(p => p.Key).Where(k => !select.Contains(k)).ToList())
            {
                ordered.Remove(key);
            }
        }

        return new ItemResponse(item.Id, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, ordered, ETags.From(item.Version));
    }
}

/// <summary>A page of items in Graph/OData shape.</summary>
public sealed record ItemPage(
    [property: JsonPropertyName("value")] IReadOnlyList<ItemResponse> Value,
    [property: JsonPropertyName("@odata.count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Count,
    [property: JsonPropertyName("@odata.nextLink"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextLink);

/// <summary>Items and folders of a list: OData queries, create, change (merge), move and delete (to the recycle bin).</summary>
internal static class ItemEndpoints
{
    public const int DefaultTop = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{ListEndpoints.Route}/{{listId:guid}}/items").WithTags("Items");
        group.MapGet("", QueryAsync).RequireScope(ListScopes.Read).WithName("ListItems")
            .WithDescription("OData query options: $filter, $orderby, $top, $skiptoken, $count and $select (field names); viewId reads through a saved view.");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.Write).WithName("CreateItem");
        group.MapGet("/{itemId:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetItem");
        group.MapGet("/{itemId:guid}/children", ChildrenAsync).RequireScope(ListScopes.Read).WithName("ListFolderChildren");
        group.MapPatch("/{itemId:guid}", UpdateAsync).RequireScope(ListScopes.Write).WithName("UpdateItem")
            .WithDescription("fields are merged (null removes a value); parentId moves the item (null: the list root); contentTypeId changes its content type. Requires If-Match.");
        group.MapDelete("/{itemId:guid}", DeleteAsync).RequireScope(ListScopes.Write).WithName("DeleteItem");
    }

    private static async Task<Results<Ok<ItemPage>, ValidationProblem, ProblemHttpResult>> QueryAsync(
        Guid workspaceId, Guid listId, HttpRequest request,
        [FromQuery(Name = "$filter")] string? filter, [FromQuery(Name = "$orderby")] string? orderBy, [FromQuery(Name = "$top")] int? top,
        [FromQuery(Name = "$skiptoken")] string? skipToken, [FromQuery(Name = "$count")] bool? count, [FromQuery(Name = "$select")] string? select,
        Guid? viewId, Caller caller, ListSchemaLoader loader, ItemQueryRunner runner, ListsDbContext db, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        if (await loader.LoadAsync(listCaller, workspaceId, listId, cancellationToken) is not { } schema)
        {
            return ApiErrors.NotFound();
        }

        if (viewId is not { } id)
        {
            return await RunAsync(listCaller, schema, request, [filter], orderBy, top, skipToken, count, select, FolderMode.All, null, runner, cancellationToken);
        }

        // A view adds its filter to the request's; its order and columns apply unless the request names its own.
        if (await ViewEndpoints.FindAsync(db, caller.TenantId, listId, id, tracking: false, cancellationToken) is not { } view)
        {
            return ApiErrors.NotFound("The view was not found.");
        }

        var columns = ViewEndpoints.Columns(view);
        return await RunAsync(
            listCaller, schema, request, [view.Filter, filter], orderBy ?? view.OrderBy, top, skipToken, count,
            select ?? (columns.Count > 0 ? string.Join(',', columns) : null), FolderMode.All, null, runner, cancellationToken);
    }

    private static async Task<Results<Ok<ItemPage>, ValidationProblem, ProblemHttpResult>> ChildrenAsync(
        Guid workspaceId, Guid listId, Guid itemId, HttpRequest request,
        [FromQuery(Name = "$filter")] string? filter, [FromQuery(Name = "$orderby")] string? orderBy, [FromQuery(Name = "$top")] int? top,
        [FromQuery(Name = "$skiptoken")] string? skipToken, [FromQuery(Name = "$count")] bool? count, [FromQuery(Name = "$select")] string? select,
        Caller caller, ListSchemaLoader loader, ItemWriter writer, ItemQueryRunner runner, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        var schema = await loader.LoadAsync(listCaller, workspaceId, listId, cancellationToken);
        var folder = schema is null ? null : await writer.FindAsync(caller.TenantId, listId, itemId, cancellationToken);
        if (folder is not { IsFolder: true } || schema!.Access.Level(folder.ScopeId) < WorkspaceAccessLevel.Read)
        {
            return ApiErrors.NotFound();
        }

        return await RunAsync(listCaller, schema, request, [filter], orderBy, top, skipToken, count, select, FolderMode.Children, itemId, runner, cancellationToken);
    }

    private static async Task<Results<Ok<ItemPage>, ValidationProblem, ProblemHttpResult>> RunAsync(
        ListCaller caller, ListSchema schema, HttpRequest request, IReadOnlyList<string?> filters, string? orderBy, int? top, string? skipToken, bool? count,
        string? select, FolderMode folders, Guid? parentId, ItemQueryRunner runner, CancellationToken cancellationToken)
    {
        var (result, error) = await runner.RunAsync(
            caller, schema, filters, orderBy, top ?? DefaultTop, skipToken, count == true, folders, parentId, cancellationToken);
        if (result is null)
        {
            return ApiErrors.Validation("query", error!);
        }

        var selected = string.IsNullOrWhiteSpace(select) ? null : select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return TypedResults.Ok(new ItemPage(
            [.. result.Items.Select(i => ItemResponse.From(i, selected))], result.Count, result.NextCursor is { } next ? NextLink(request, next) : null));
    }

    private static string NextLink(HttpRequest request, string cursor)
    {
        var query = request.Query
            .Where(q => q.Key is not ("$skiptoken" or "$count"))
            .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}")
            .Append($"$skiptoken={cursor}");
        return $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}?{string.Join('&', query)}";
    }

    private static async Task<Results<Ok<ItemResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, ListSchemaLoader loader, ItemWriter writer, HttpResponse response, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken);
        var item = schema is null ? null : await writer.FindAsync(caller.TenantId, listId, itemId, cancellationToken);
        if (item is null || schema!.Access.Level(item.ScopeId) < WorkspaceAccessLevel.Read)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, item.Version);
        return TypedResults.Ok(ItemResponse.From(item));
    }

    private static async Task<Results<Created<ItemResponse>, Ok<ItemResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, Guid listId, CreateItemRequest request, Caller caller, ListSchemaLoader loader, ItemWriter writer, IServiceProvider services,
        HttpResponse response, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        if (await loader.LoadAsync(listCaller, workspaceId, listId, cancellationToken) is not { } schema)
        {
            return ApiErrors.NotFound();
        }

        if (request.Id is { } id)
        {
            if (id == Guid.Empty)
            {
                return ApiErrors.Validation("id", "The id cannot be empty.");
            }

            // A repeated create answers with the item it made; any other item with this id (also a deleted one) is a conflict.
            var store = (ListItemStore)services.GetRequiredService<Contracts.IListItemStore>();
            if (await store.FindAnyAsync(caller.TenantId, id, cancellationToken) is { } existing)
            {
                if (existing.ListId != listId || existing.DeletedAt is not null || schema.Access.Level(existing.ScopeId) < WorkspaceAccessLevel.Read)
                {
                    return ApiErrors.Conflict("idTaken", "An item with this id exists in another list or was deleted.");
                }

                ETags.Set(response, existing.Version);
                return TypedResults.Ok(ItemResponse.From(existing));
            }
        }

        var values = request.Fields is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(request.Fields, ListsJson.Default.JsonObject);
        ItemWriteResult result;
        try
        {
            result = await writer.CreateAsync(listCaller, schema, request.ContentTypeId, request.ParentId, request.IsFolder, values, request.Id ?? Ids.New(), cancellationToken);
        }
        catch (DbUpdateException) when (request.Id is not null)
        {
            // The id is used by an item of another tenant.
            return ApiErrors.Conflict("idTaken", "An item with this id exists in another list or was deleted.");
        }

        if (Problem(result) is { } problem)
        {
            return problem;
        }

        if (result.Errors is { } errors)
        {
            return ApiErrors.Validation(errors);
        }

        ETags.Set(response, result.Item!.Version);
        return TypedResults.Created($"/v1.0/workspaces/{workspaceId}/lists/{listId}/items/{result.Item.Id}", ItemResponse.From(result.Item));
    }

    private static async Task<Results<Ok<ItemResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid workspaceId, Guid listId, Guid itemId, JsonElement body, Caller caller, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer,
        HttpRequest http, HttpResponse response, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return ApiErrors.Validation("body", "A JSON object is expected.");
        }

        var listCaller = ListEndpoints.CallerOf(caller);
        var (schema, item, problem) = await LoadForChangeAsync(listCaller, workspaceId, listId, itemId, loader, db, http, cancellationToken);
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
                case "contentTypeId" when property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var type):
                    contentTypeId = type;
                    break;
                case "parentId" when property.Value.ValueKind == JsonValueKind.Null:
                    parentId = Optional<Guid?>.Of(null);
                    break;
                case "parentId" when property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var parent):
                    parentId = Optional<Guid?>.Of(parent);
                    break;
                default:
                    return ApiErrors.Validation(property.Name, "Unknown or invalid property.");
            }
        }

        try
        {
            var result = await writer.UpdateAsync(listCaller, schema!, item!, contentTypeId, parentId, fields, cancellationToken);
            if (Problem(result) is { } rejected)
            {
                return rejected;
            }

            if (result.Errors is { } errors)
            {
                return ApiErrors.Validation(errors);
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
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer,
        HttpRequest http, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        var (schema, item, problem) = await LoadForChangeAsync(listCaller, workspaceId, listId, itemId, loader, db, http, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        try
        {
            var result = await writer.DeleteAsync(listCaller, schema!, item!, cancellationToken);
            return result switch
            {
                { Conflict: { } conflict } => ApiErrors.Conflict("folderNotEmpty", conflict),
                { Cancelled: { } message } => CancelledByMutator(message),
                _ => TypedResults.NoContent(),
            };
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }
    }

    private static ProblemHttpResult? Problem(ItemWriteResult result) => result switch
    {
        { Forbidden: true } => ListEndpoints.Forbidden(),
        { Cancelled: { } message } => CancelledByMutator(message),
        { Conflict: { } message } => ApiErrors.Conflict("invalidMove", message),
        _ => null,
    };

    internal static ProblemHttpResult CancelledByMutator(string message) => ApiErrors.Conflict("cancelledByMutator", message);

    /// <summary>A tracked item of the list that is not deleted.</summary>
    internal static Task<ListItem?> FindTrackedAsync(ListsDbContext database, Guid tenantId, Guid listId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var list = listId;
        var id = itemId;
        var ct = cancellationToken;
        return db.Items.Where(i => i.TenantId == tenant && i.ListId == list && i.Id == id && i.DeletedAt == null).FirstOrDefaultAsync(ct);
    }

    internal static async Task<(ListSchema? Schema, ListItem? Item, ProblemHttpResult? Problem)> LoadForChangeAsync(
        ListCaller caller, Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, HttpRequest http, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        var item = schema is null ? null : await FindTrackedAsync(db, caller.TenantId, listId, itemId, cancellationToken);
        var level = item is null ? WorkspaceAccessLevel.None : schema!.Access.Level(item.ScopeId);
        if (level == WorkspaceAccessLevel.None)
        {
            return (null, null, ApiErrors.NotFound());
        }

        if (level < WorkspaceAccessLevel.Contribute)
        {
            return (null, null, ListEndpoints.Forbidden());
        }

        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return (null, null, ApiErrors.PreconditionRequired());
        }

        return version != item!.Version ? (null, null, ApiErrors.PreconditionFailed()) : (schema, item, null);
    }
}
