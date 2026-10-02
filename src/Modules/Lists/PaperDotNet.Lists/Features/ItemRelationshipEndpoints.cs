using System.Text.Json.Nodes;
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

public sealed record AddItemRelationshipRequest(Guid OtherId, string? Type = null, bool? Directed = null, JsonObject? Attributes = null);
public sealed record ItemRelationshipResponse(Guid Id, Guid SourceItemId, Guid TargetItemId, bool Directed, RelationshipTypeData? Type, LocatedItemResponse RelatedItem, JsonObject Attributes, uint Version);

public sealed record UpdateItemRelationshipRequest(JsonObject Attributes);
public sealed record WorkspaceRelationshipResponse(Guid Id, bool Directed, RelationshipTypeData? Type, JsonObject Attributes, uint Version, LocatedItemResponse SourceItem, LocatedItemResponse TargetItem);

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
        endpoints.MapV1Group("workspaces/{workspaceId:guid}/relationships", "Workspace relationships")
            .MapGet("", QueryAsync).RequireScope(ListScopes.Read).WithName("QueryWorkspaceRelationships").WithQueryOptions(QueryOptions.Paging | QueryOptions.Filter);
        edges.MapGet("/{relationshipId:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetItemRelationship");
        edges.MapPatch("/{relationshipId:guid}", UpdateAsync).RequireScope(ListScopes.Write).WithName("UpdateItemRelationship");
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
            var result = await items.AddRelationshipAsync(itemId, request.OtherId, new(request.Type, request.Directed, request.Attributes), ct);
            return result.Succeeded ? TypedResults.NoContent() : Problem(result);
        }
        catch (ArgumentException ex) { return ApiErrors.Problem(400, "invalidRelationship", ex.Message); }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid itemId, Guid relationshipId, IListItemStore items, CancellationToken ct)
    {
        var result = await items.RemoveRelationshipAsync(itemId, relationshipId, ct);
        return result.Succeeded ? TypedResults.NoContent() : Problem(result);
    }

    private static async Task<Results<Ok<ItemRelationshipResponse>, ProblemHttpResult>> GetAsync(Guid itemId, Guid relationshipId, IListItemStore store,
        ListsDbContext db, ListSchemaLoader loader, IWorkspaceAccess workspaces, HttpResponse response, CancellationToken ct)
    {
        var edge = await store.GetRelationshipAsync(itemId, relationshipId, ct);
        if (edge is null) return ApiErrors.NotFound();
        var located = await LocateAsync([edge.RelatedItem.Id], db, loader, workspaces, ct);
        if (!located.TryGetValue(edge.RelatedItem.Id, out var peer)) return ApiErrors.NotFound();
        ETags.Set(response, edge.Version);
        return TypedResults.Ok(new ItemRelationshipResponse(edge.Id, edge.SourceItemId, edge.TargetItemId, edge.Directed, edge.Type, peer, edge.Attributes, edge.Version));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> UpdateAsync(Guid itemId, Guid relationshipId, UpdateItemRelationshipRequest request,
        IListItemStore store, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (!ETags.TryGetIfMatch(http, out var version)) return ApiErrors.PreconditionRequired();
        if (request.Attributes is null) return ApiErrors.Problem(400, "invalidRelationshipAttributes", "Provide an attributes object.");
        try
        {
            var result = await store.UpdateRelationshipAsync(itemId, relationshipId, request.Attributes, version, ct);
            if (!result.Succeeded) return Problem(result);
            ETags.Set(response, version + 1);
            return TypedResults.NoContent();
        }
        catch (ArgumentException ex) { return ApiErrors.Problem(400, "invalidRelationshipAttributes", ex.Message); }
    }

    private static async Task<Results<Ok<Page<WorkspaceRelationshipResponse>>, ProblemHttpResult>> QueryAsync(Guid workspaceId, string? type, bool? directed,
        IListItemStore store, ListsDbContext db, ListSchemaLoader loader, IWorkspaceAccess workspaces, HttpRequest http, CancellationToken ct)
    {
        var request = PageRequest.From(http);
        WorkspaceRelationshipPage? page;
        try { page = await store.QueryRelationshipsAsync(workspaceId, type, directed, http.Query["$filter"].FirstOrDefault(), request.Top, request.After, ct); }
        catch (ArgumentException ex) { return ApiErrors.Problem(400, "invalidRelationshipQuery", ex.Message); }
        if (page is null) return ApiErrors.NotFound();
        var located = await LocateAsync(page.Items.SelectMany(e => new[] { e.SourceItem.Id, e.TargetItem.Id }).Distinct().ToArray(), db, loader, workspaces, ct);
        var entries = page.Items.Where(e => located.ContainsKey(e.SourceItem.Id) && located.ContainsKey(e.TargetItem.Id)
            && (located[e.SourceItem.Id].WorkspaceId == workspaceId || located[e.TargetItem.Id].WorkspaceId == workspaceId))
            .Select(e => new WorkspaceRelationshipResponse(e.Id, e.Directed, e.Type, e.Attributes, e.Version, located[e.SourceItem.Id], located[e.TargetItem.Id])).ToList();
        return TypedResults.Ok(new Page<WorkspaceRelationshipResponse>(entries, NextLink(http, page.NextCursor)));
    }

    private static async Task<Dictionary<Guid, LocatedItemResponse>> LocateAsync(Guid[] ids, ListsDbContext db, ListSchemaLoader loader, IWorkspaceAccess workspaces, CancellationToken ct)
    {
        var items = await db.Items.AsNoTracking().Where(i => EF.Parameter(ids).Contains(i.Id)).ToListAsync(ct);
        var schemas = (await loader.LoadManyAsync(items.Select(i => i.ListId).Distinct().ToArray(), false, ct)).ToDictionary(s => s.List.Id);
        var names = await workspaces.GetNamesAsync(schemas.Values.Select(s => s.List.WorkspaceId).Distinct().ToArray(), ct);
        return items.Where(i => schemas.TryGetValue(i.ListId, out var schema) && schema.Access.Level(i.ScopeId) >= WorkspaceAccessLevel.Read)
            .ToDictionary(i => i.Id, i => GlobalItemEndpoints.Located(schemas[i.ListId], i, names));
    }

    private static string? NextLink(HttpRequest http, string? cursor)
    {
        if (cursor is null) return null;
        var query = http.Query.Where(q => q.Key != "$skiptoken").Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}");
        return $"{http.Scheme}://{http.Host}{http.PathBase}{http.Path}?{string.Join('&', query.Append($"$skiptoken={cursor}"))}";
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
                GlobalItemEndpoints.Located(schemas[peers[e.RelatedItem.Id].ListId], peers[e.RelatedItem.Id], names), e.Attributes, e.Version)).ToList();
        var query = http.Query.Where(q => q.Key != "$skiptoken").Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}");
        var next = page.NextCursor is { } cursor ? $"{http.Scheme}://{http.Host}{http.PathBase}{http.Path}?{string.Join('&', query.Append($"$skiptoken={cursor}"))}" : null;
        return TypedResults.Ok(new Page<ItemRelationshipResponse>(entries, next));
    }
}
