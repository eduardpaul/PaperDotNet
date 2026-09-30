using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Core.Api;
using PaperDotNet.Core.Host.Data;
using PaperDotNet.Core.Messaging;
using PaperDotNet.Core.Security;

namespace PaperDotNet.Core.Host.Lists;

public sealed record ItemDto(
    Guid Id,
    Guid ListId,
    JsonObject Fields,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedBy,
    [property: JsonPropertyName("@odata.etag")] string ETag);

/// <summary>New item: <c>fields</c> holds the title and the other field values.</summary>
public sealed record CreateItemRequest(JsonObject Fields);

/// <summary>Changed values; <c>null</c> removes a value.</summary>
public sealed record UpdateItemRequest(JsonObject Fields);

/// <summary>List items (Graph-shaped: values under <c>fields</c>). Queries copy their arguments into locals (ADR-0039).</summary>
internal static class ItemEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/lists/{listId:guid}/items").WithTags("Items");
        group.MapGet("/", QueryAsync).RequireScope(Scopes.ListsRead).WithName("ListItems")
            .WithDescription("OData query options: $filter, $orderby, $top, $skiptoken, $count.");
        group.MapPost("/", CreateAsync).RequireScope(Scopes.ListsWrite).WithName("CreateItem");
        group.MapGet("/{itemId:guid}", GetAsync).RequireScope(Scopes.ListsRead).WithName("GetItem");
        group.MapPatch("/{itemId:guid}", UpdateAsync).RequireScope(Scopes.ListsWrite).WithName("UpdateItem");
        group.MapDelete("/{itemId:guid}", DeleteAsync).RequireScope(Scopes.ListsWrite).WithName("DeleteItem");
    }

    private static ItemDto ToDto(ListItem item, IReadOnlyList<FieldDefinition> fields) =>
        new(item.Id, item.ListId, FieldValues.ForApi(fields, item.Title, item.Fields), item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, ETags.From(item.Version));

    private static Task<ListItem?> FindAsync(CoreDb database, Guid tenantId, Guid listId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var list = listId;
        var id = itemId;
        var ct = cancellationToken;
        return db.Items.Where(i => i.TenantId == tenant && i.ListId == list && i.Id == id).FirstOrDefaultAsync(ct);
    }

    private static async Task<Results<Ok<Page<ItemDto>>, ProblemHttpResult>> QueryAsync(
        Guid listId,
        [FromQuery(Name = "$filter")] string? filter,
        [FromQuery(Name = "$orderby")] string? orderBy,
        [FromQuery(Name = "$top")] int? top,
        [FromQuery(Name = "$skiptoken")] string? skipToken,
        [FromQuery(Name = "$count")] bool? count,
        HttpRequest request,
        ClaimsPrincipalAccessor caller,
        CoreDb db,
        IItemQueries queries,
        CancellationToken cancellationToken)
    {
        if (await ListEndpoints.FindAsync(db, caller.TenantId, listId, cancellationToken) is not { } list)
        {
            return ApiErrors.NotFound();
        }

        var fields = ListEndpoints.FieldsOf(list);
        var (filterClause, orderByClause, error) = ItemQueryParser.Parse(filter, orderBy, fields);
        if (error is not null)
        {
            return ApiErrors.BadRequest("invalidQuery", error);
        }

        var page = PageRequest.Create(top, skipToken);
        ItemQueryResult result;
        try
        {
            result = await queries.QueryAsync(new ItemQuery(caller.TenantId, listId, filterClause, orderByClause, page, count ?? false), cancellationToken);
        }
        catch (NotSupportedException exception)
        {
            return ApiErrors.BadRequest("invalidQuery", exception.Message);
        }

        var items = result.Items.Select(i => ToDto(i, fields)).ToList();
        return TypedResults.Ok(orderByClause is null
            ? Page.Create(items, page, request, i => i.Id, result.Count)
            : Page.CreateAtOffset(items, page, request, result.Count));
    }

    private static async Task<Results<Created<ItemDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid listId, CreateItemRequest body, ClaimsPrincipalAccessor caller, CoreDb db, IOutbox outbox, CancellationToken cancellationToken)
    {
        if (await ListEndpoints.FindAsync(db, caller.TenantId, listId, cancellationToken) is not { } list)
        {
            return ApiErrors.NotFound();
        }

        var fields = ListEndpoints.FieldsOf(list);
        var stored = new JsonObject();
        var title = "";
        if (FieldValues.Merge(fields, body.Fields ?? [], stored, ref title) is { Count: > 0 } errors)
        {
            return ApiErrors.Validation(errors);
        }

        var item = new ListItem { Id = Ids.New(), TenantId = caller.TenantId, ListId = list.Id, Title = title, Fields = stored.ToJsonString() };
        db.Items.Add(item);
        await outbox.SaveChangesAsync(db, [new ItemCreated(list.Id, item.Id, title) { TenantId = caller.TenantId, UserId = caller.UserId }], cancellationToken);
        return TypedResults.Created($"/v1.0/lists/{list.Id}/items/{item.Id}", ToDto(item, fields));
    }

    private static async Task<Results<Ok<ItemDto>, ProblemHttpResult>> GetAsync(
        Guid listId, Guid itemId, HttpResponse response, ClaimsPrincipalAccessor caller, CoreDb db, CancellationToken cancellationToken)
    {
        if (await ListEndpoints.FindAsync(db, caller.TenantId, listId, cancellationToken) is not { } list
            || await FindAsync(db, caller.TenantId, listId, itemId, cancellationToken) is not { } item)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, item.Version);
        return TypedResults.Ok(ToDto(item, ListEndpoints.FieldsOf(list)));
    }

    private static async Task<Results<Ok<ItemDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid listId, Guid itemId, UpdateItemRequest body, HttpContext http, ClaimsPrincipalAccessor caller, CoreDb db, IOutbox outbox, CancellationToken cancellationToken)
    {
        if (!ETags.TryGetIfMatch(http.Request, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (await ListEndpoints.FindAsync(db, caller.TenantId, listId, cancellationToken) is not { } list
            || await FindAsync(db, caller.TenantId, listId, itemId, cancellationToken) is not { } item)
        {
            return ApiErrors.NotFound();
        }

        if (item.Version != version)
        {
            return ApiErrors.PreconditionFailed();
        }

        var fields = ListEndpoints.FieldsOf(list);
        var stored = JsonNode.Parse(item.Fields)?.AsObject() ?? [];
        var title = item.Title;
        var input = body.Fields ?? [];
        if (FieldValues.Merge(fields, input, stored, ref title) is { Count: > 0 } errors)
        {
            return ApiErrors.Validation(errors);
        }

        item.Title = title;
        item.Fields = stored.ToJsonString();
        var changed = input.Select(p => p.Key).ToList();
        try
        {
            await outbox.SaveChangesAsync(db, [new ItemUpdated(list.Id, item.Id, title, changed) { TenantId = caller.TenantId, UserId = caller.UserId }], cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(http.Response, item.Version);
        return TypedResults.Ok(ToDto(item, fields));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid listId, Guid itemId, HttpRequest request, ClaimsPrincipalAccessor caller, CoreDb db, IOutbox outbox, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, caller.TenantId, listId, itemId, cancellationToken) is not { } item)
        {
            return ApiErrors.NotFound();
        }

        if (ETags.HasIfMatch(request) && (!ETags.TryGetIfMatch(request, out var version) || version != item.Version))
        {
            return ApiErrors.PreconditionFailed();
        }

        db.Items.Remove(item);
        await outbox.SaveChangesAsync(db, [new ItemDeleted(listId, item.Id, item.Title) { TenantId = caller.TenantId, UserId = caller.UserId }], cancellationToken);
        return TypedResults.NoContent();
    }
}
