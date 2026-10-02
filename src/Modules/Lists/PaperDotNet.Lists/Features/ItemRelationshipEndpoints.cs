using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

public sealed record AddItemRelationshipRequest(Guid OtherId, string? Type = null, bool? Directed = null);
public sealed record ItemRelationshipResponse(Guid Id, Guid SourceItemId, Guid TargetItemId, bool Directed, RelationshipTypeData? Type, LocatedItemResponse RelatedItem);

internal static class ItemRelationshipEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var types = endpoints.MapV1Group("relationshipTypes", "Relationship types");
        types.MapGet("", async (IListItemStore items, CancellationToken ct) => TypedResults.Ok(await items.GetRelationshipTypesAsync(ct)))
            .RequireScope(ListScopes.Read).WithName("ListRelationshipTypes");
        types.MapPost("", CreateTypeAsync).RequireScope(ListScopes.Write).WithName("CreateRelationshipType");
        var edges = endpoints.MapV1Group("items/{itemId:guid}/relationships", "Item relationships");
        edges.MapGet("", ListAsync).RequireScope(ListScopes.Read).WithName("ListItemRelationships").WithQueryOptions(QueryOptions.Paging);
        edges.MapPost("", AddAsync).RequireScope(ListScopes.Write).WithName("AddItemRelationship");
        edges.MapDelete("/{relationshipId:guid}", DeleteAsync).RequireScope(ListScopes.Write).WithName("RemoveItemRelationship");
    }

    private static async Task<Results<Ok<RelationshipTypeData>, ProblemHttpResult>> CreateTypeAsync(RelationshipTypeOptions request, IListItemStore items, CancellationToken ct)
    {
        try { return TypedResults.Ok(await items.EnsureRelationshipTypeAsync(request, ct)); }
        catch (ArgumentException ex) { return ApiErrors.Problem(400, "invalidRelationshipType", ex.Message); }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> AddAsync(Guid itemId, AddItemRelationshipRequest request, IListItemStore items, CancellationToken ct)
    {
        try
        {
            var result = await items.AddRelationshipAsync(itemId, request.OtherId, new(request.Type, request.Directed), ct);
            return result.Succeeded ? TypedResults.NoContent() : Problem(result);
        }
        catch (ArgumentException ex) { return ApiErrors.Problem(400, "invalidRelationship", ex.Message); }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid itemId, Guid relationshipId, IListItemStore items, CancellationToken ct)
    {
        var result = await items.RemoveRelationshipAsync(itemId, relationshipId, ct);
        return result.Succeeded ? TypedResults.NoContent() : Problem(result);
    }

    private static ProblemHttpResult Problem(ListItemResult result) => result.Status switch
    {
        ListItemStatus.NotFound => ApiErrors.NotFound(),
        ListItemStatus.Forbidden => ListEndpoints.Forbidden(),
        ListItemStatus.VersionMismatch => ApiErrors.PreconditionFailed(),
        ListItemStatus.Rejected => ApiErrors.Conflict("relationshipLimit", result.Describe()),
        _ => ApiErrors.Problem(400, "invalidRelationship", result.Describe()),
    };

    private static async Task<Results<Ok<Page<ItemRelationshipResponse>>, ProblemHttpResult>> ListAsync(
        Guid itemId, string? type, string? direction, IListItemStore store, ListsDbContext db, ListSchemaLoader loader,
        IWorkspaceAccess workspaces, HttpRequest http, CancellationToken ct)
    {
        var request = PageRequest.From(http);
        ItemRelationshipPage? page;
        try { page = await store.GetRelationshipsAsync(itemId, type, direction, request.Top, request.After, ct); }
        catch (ArgumentException ex) { return ApiErrors.Problem(400, "invalidRelationshipQuery", ex.Message); }
        if (page is null) return ApiErrors.NotFound();
        var ids = page.Items.Select(e => e.RelatedItem.Id).Distinct().ToArray();
        var peers = await db.Items.AsNoTracking().Where(i => EF.Parameter(ids).Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var schemas = (await loader.LoadManyAsync(peers.Values.Select(i => i.ListId).Distinct().ToArray(), false, ct)).ToDictionary(s => s.List.Id);
        var names = await workspaces.GetNamesAsync(schemas.Values.Select(s => s.List.WorkspaceId).Distinct().ToArray(), ct);
        var entries = page.Items.Where(e => peers.TryGetValue(e.RelatedItem.Id, out var peer)
            && schemas.TryGetValue(peer.ListId, out var schema) && schema.Access.Level(peer.ScopeId) >= WorkspaceAccessLevel.Read).Select(e =>
            new ItemRelationshipResponse(e.Id, e.SourceItemId, e.TargetItemId, e.Directed, e.Type,
                GlobalItemEndpoints.Located(schemas[peers[e.RelatedItem.Id].ListId], peers[e.RelatedItem.Id], names))).ToList();
        var query = http.Query.Where(q => q.Key != "$skiptoken").Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}");
        var next = page.NextCursor is { } cursor ? $"{http.Scheme}://{http.Host}{http.PathBase}{http.Path}?{string.Join('&', query.Append($"$skiptoken={cursor}"))}" : null;
        return TypedResults.Ok(new Page<ItemRelationshipResponse>(entries, next));
    }
}
