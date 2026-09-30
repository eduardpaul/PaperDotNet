using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Querying;

namespace PaperDotNet.Lists.Features;

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

/// <summary>List items (Graph-shaped: values under <c>fields</c>), written through <see cref="ListItemStore"/>.</summary>
internal static class ItemEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/lists/{listId:guid}/items").WithTags("Items");
        group.MapGet("/", QueryAsync).RequireScope(ListScopes.Read).WithName("ListItems")
            .WithDescription("OData query options: $filter, $orderby, $top, $skiptoken, $count.");
        group.MapPost("/", CreateAsync).RequireScope(ListScopes.Write).WithName("CreateItem");
        group.MapGet("/{itemId:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetItem");
        group.MapPatch("/{itemId:guid}", UpdateAsync).RequireScope(ListScopes.Write).WithName("UpdateItem");
        group.MapDelete("/{itemId:guid}", DeleteAsync).RequireScope(ListScopes.Write).WithName("DeleteItem");
    }

    private static ItemDto ToDto(ListItemData item) =>
        new(item.Id, item.ListId, item.Fields, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, ETags.From(item.Version));

    private static async Task<Results<Ok<Page<ItemDto>>, ProblemHttpResult>> QueryAsync(
        Guid listId,
        [FromQuery(Name = "$filter")] string? filter,
        [FromQuery(Name = "$orderby")] string? orderBy,
        [FromQuery(Name = "$top")] int? top,
        [FromQuery(Name = "$skiptoken")] string? skipToken,
        [FromQuery(Name = "$count")] bool? count,
        HttpRequest request,
        Caller caller,
        ListItemStore items,
        IItemQueries queries,
        CancellationToken cancellationToken)
    {
        if (await items.FindListEntityAsync(caller.TenantId, listId, cancellationToken) is not { } list)
        {
            return ApiErrors.NotFound();
        }

        var fields = ListItemStore.FieldsOf(list);
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

        var dtos = result.Items.Select(i => ToDto(ListItemStore.ToData(i, fields))).ToList();
        return TypedResults.Ok(orderByClause is null
            ? Page.Create(dtos, page, request, i => i.Id, result.Count)
            : Page.CreateAtOffset(dtos, page, request, result.Count));
    }

    private static async Task<Results<Created<ItemDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid listId, CreateItemRequest body, Caller caller, ListItemStore items, CancellationToken cancellationToken)
    {
        var result = await items.CreateAsync(caller.Actor, listId, null, body.Fields ?? [], cancellationToken);
        return result.Status switch
        {
            ItemWriteStatus.Ok => TypedResults.Created($"/v1.0/lists/{listId}/items/{result.Item!.Id}", ToDto(result.Item)),
            ItemWriteStatus.Invalid => ApiErrors.Validation(result.Errors!.ToDictionary()),
            _ => ApiErrors.NotFound(),
        };
    }

    private static async Task<Results<Ok<ItemDto>, ProblemHttpResult>> GetAsync(
        Guid listId, Guid itemId, HttpResponse response, Caller caller, ListItemStore items, CancellationToken cancellationToken)
    {
        if (await items.GetAsync(caller.TenantId, listId, itemId, cancellationToken) is not { } item)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, item.Version);
        return TypedResults.Ok(ToDto(item));
    }

    private static async Task<Results<Ok<ItemDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid listId, Guid itemId, UpdateItemRequest body, HttpContext http, Caller caller, ListItemStore items, CancellationToken cancellationToken)
    {
        if (!ETags.TryGetIfMatch(http.Request, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        var result = await items.UpdateAsync(caller.Actor, listId, itemId, body.Fields ?? [], version, cancellationToken);
        switch (result.Status)
        {
            case ItemWriteStatus.Ok:
                ETags.Set(http.Response, result.Item!.Version);
                return TypedResults.Ok(ToDto(result.Item));
            case ItemWriteStatus.Invalid:
                return ApiErrors.Validation(result.Errors!.ToDictionary());
            case ItemWriteStatus.PreconditionFailed:
                return ApiErrors.PreconditionFailed();
            default:
                return ApiErrors.NotFound();
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid listId, Guid itemId, HttpRequest request, Caller caller, ListItemStore items, CancellationToken cancellationToken)
    {
        uint? ifMatch = null;
        if (ETags.HasIfMatch(request))
        {
            if (!ETags.TryGetIfMatch(request, out var version))
            {
                return ApiErrors.PreconditionFailed();
            }

            ifMatch = version;
        }

        var result = await items.DeleteAsync(caller.Actor, listId, itemId, ifMatch, cancellationToken);
        return result.Status switch
        {
            ItemWriteStatus.Ok => TypedResults.NoContent(),
            ItemWriteStatus.PreconditionFailed => ApiErrors.PreconditionFailed(),
            _ => ApiErrors.NotFound(),
        };
    }
}
