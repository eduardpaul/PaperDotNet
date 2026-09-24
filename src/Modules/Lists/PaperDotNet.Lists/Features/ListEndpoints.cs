using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

public sealed record ListSummary(Guid Id, Guid WorkspaceId, string Name, string? Description, ListKind Kind, bool AllowFolders, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A list with its content types and effective columns.</summary>
public sealed record ListResponse(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string? Description,
    ListKind Kind,
    bool AllowFolders,
    IReadOnlyList<ContentTypeResponse> ContentTypes,
    IReadOnlyList<FieldDefinitionDto> Columns,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateListRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(2000)] string? Description,
    ListKind Kind = ListKind.List,
    bool AllowFolders = true,
    IReadOnlyList<Guid>? ContentTypeIds = null);

public sealed record UpdateListRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Name,
    [property: StringLength(2000)] string? Description,
    bool? AllowFolders);

public sealed record AddListContentTypeRequest([property: Required] Guid ContentTypeId);

internal static class ListEndpoints
{
    public const string Route = "workspaces/{workspaceId:guid}/lists";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group(Route, "Lists");
        group.MapGet("", ListAsync).RequireScope(ListScopes.Read).WithName("ListLists");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.Read).WithName("CreateList");
        group.MapGet("/{listId:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetList");
        group.MapPatch("/{listId:guid}", UpdateAsync).RequireScope(ListScopes.Read).WithName("UpdateList");
        group.MapDelete("/{listId:guid}", DeleteAsync).RequireScope(ListScopes.Read).WithName("DeleteList");
        group.MapPost("/{listId:guid}/contentTypes", AddContentTypeAsync).RequireScope(ListScopes.Read).WithName("AddListContentType");
        group.MapDelete("/{listId:guid}/contentTypes/{contentTypeId:guid}", RemoveContentTypeAsync).RequireScope(ListScopes.Read).WithName("RemoveListContentType");
    }

    private static async Task<Results<Ok<List<ListSummary>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, IWorkspaceAccess workspaces, ListsDbContext db, CancellationToken ct)
    {
        if (await workspaces.GetPermissionAsync(workspaceId, ct) == WorkspaceAccessLevel.None)
        {
            return ApiErrors.NotFound();
        }

        var lists = await db.Lists.AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId)
            .OrderBy(l => l.Name)
            .Select(l => new ListSummary(l.Id, l.WorkspaceId, l.Name, l.Description, l.Kind, l.AllowFolders, l.CreatedAt, l.UpdatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(lists);
    }

    private static async Task<Results<Created<ListResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, CreateListRequest request, IWorkspaceAccess workspaces, ListsDbContext db, HttpResponse response, CancellationToken ct)
    {
        var permission = await workspaces.GetPermissionAsync(workspaceId, ct);
        if (permission == WorkspaceAccessLevel.None)
        {
            return ApiErrors.NotFound();
        }

        if (permission < WorkspaceAccessLevel.Manage)
        {
            return Forbidden();
        }

        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var contentTypeIds = request.ContentTypeIds?.Distinct().ToList() ?? [];
        if (contentTypeIds.Count == 0)
        {
            contentTypeIds.Add(await EnsureItemContentTypeAsync(db, ct));
        }

        var contentTypes = await db.ContentTypes.AsNoTracking().Where(c => contentTypeIds.Contains(c.Id)).ToListAsync(ct);
        if (contentTypes.Count != contentTypeIds.Count)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["contentTypeIds"] = ["Unknown content type."] });
        }

        var fields = new List<FieldDefinition>();
        foreach (var contentType in contentTypeIds.Select(id => contentTypes.First(c => c.Id == id)))
        {
            if (ListSchema.FindConflict(fields, contentType) is { } conflict)
            {
                return ApiErrors.Conflict("fieldConflict", conflict);
            }

            fields.AddRange(contentType.Fields);
        }

        var list = new ListDefinition
        {
            Id = Ids.New(),
            WorkspaceId = workspaceId,
            Name = request.Name.Trim(),
            Description = request.Description,
            Kind = request.Kind,
            AllowFolders = request.AllowFolders,
            ContentTypeIds = contentTypeIds,
        };
        db.Lists.Add(list);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, list.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/lists/{list.Id}", ToResponse(new ListSchema(list, contentTypes, permission)));
    }

    private static async Task<Results<Ok<ListResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid listId, ListSchemaLoader loader, HttpResponse response, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, schema.List.Version);
        return TypedResults.Ok(ToResponse(schema));
    }

    private static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid workspaceId, Guid listId, UpdateListRequest request, ListSchemaLoader loader, ListsDbContext db,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var (schema, problem) = await LoadForChangeAsync(workspaceId, listId, loader, db, http, ct);
        if (problem is not null)
        {
            return problem;
        }

        var list = schema!.List;
        list.Name = request.Name?.Trim() ?? list.Name;
        list.Description = request.Description ?? list.Description;
        list.AllowFolders = request.AllowFolders ?? list.AllowFolders;
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        ETags.Set(response, list.Version);
        return TypedResults.Ok(ToResponse(schema));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid listId, ListSchemaLoader loader, ListsDbContext db, HttpRequest http, CancellationToken ct)
    {
        var (schema, problem) = await LoadForChangeAsync(workspaceId, listId, loader, db, http, ct);
        if (problem is not null)
        {
            return problem;
        }

        db.Lists.Remove(schema!.List);
        return await SaveAsync(db, ct) is { } conflict ? conflict : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ListResponse>, ProblemHttpResult>> AddContentTypeAsync(
        Guid workspaceId, Guid listId, AddListContentTypeRequest request, ListSchemaLoader loader, ListsDbContext db,
        HttpResponse response, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct, tracking: true);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Manage)
        {
            return Forbidden();
        }

        var contentType = await db.ContentTypes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ContentTypeId, ct);
        if (contentType is null)
        {
            return ApiErrors.NotFound("The content type was not found.");
        }

        if (!schema.List.ContentTypeIds.Contains(contentType.Id))
        {
            if (ListSchema.FindConflict(schema.Fields.Values, contentType) is { } conflict)
            {
                return ApiErrors.Conflict("fieldConflict", conflict);
            }

            schema.List.ContentTypeIds = [.. schema.List.ContentTypeIds, contentType.Id];
            await db.SaveChangesAsync(ct);
        }

        var updated = await loader.LoadAsync(workspaceId, listId, ct);
        ETags.Set(response, updated!.List.Version);
        return TypedResults.Ok(ToResponse(updated));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveContentTypeAsync(
        Guid workspaceId, Guid listId, Guid contentTypeId, ListSchemaLoader loader, ListsDbContext db, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct, tracking: true);
        if (schema is null || !schema.List.ContentTypeIds.Contains(contentTypeId))
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Manage)
        {
            return Forbidden();
        }

        if (schema.List.ContentTypeIds.Count == 1)
        {
            return ApiErrors.Conflict("lastContentType", "A list needs at least one content type.");
        }

        if (await db.Items.AnyAsync(i => i.ListId == listId && i.ContentTypeId == contentTypeId, ct))
        {
            return ApiErrors.Conflict("contentTypeInUse", "Items in this list still use the content type.");
        }

        schema.List.ContentTypeIds = schema.List.ContentTypeIds.Where(id => id != contentTypeId).ToList();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Loads a list the caller may restructure, enforcing <c>If-Match</c>.</summary>
    private static async Task<(ListSchema? Schema, ProblemHttpResult? Problem)> LoadForChangeAsync(
        Guid workspaceId, Guid listId, ListSchemaLoader loader, ListsDbContext db, HttpRequest http, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct, tracking: true);
        if (schema is null)
        {
            return (null, ApiErrors.NotFound());
        }

        if (schema.Permission < WorkspaceAccessLevel.Manage)
        {
            return (null, Forbidden());
        }

        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return (null, ApiErrors.PreconditionRequired());
        }

        if (version != schema.List.Version)
        {
            return (null, ApiErrors.PreconditionFailed());
        }

        db.Entry(schema.List).Property(l => l.Version).OriginalValue = version;
        return (schema, null);
    }

    private static async Task<ProblemHttpResult?> SaveAsync(ListsDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }
    }

    /// <summary>The built-in Item content type (created on demand for tenants that predate the Lists module).</summary>
    private static async Task<Guid> EnsureItemContentTypeAsync(ListsDbContext db, CancellationToken ct)
    {
        var id = await db.ContentTypes.Where(c => c.IsBuiltIn && c.Name == ContentType.ItemName).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (id is { } existing)
        {
            return existing;
        }

        var item = new ContentType { Id = Ids.New(), Name = ContentType.ItemName, Description = "A generic item with a title.", IsBuiltIn = true };
        db.ContentTypes.Add(item);
        await db.SaveChangesAsync(ct);
        return item.Id;
    }

    internal static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.");

    private static ListResponse ToResponse(ListSchema schema) => new(
        schema.List.Id,
        schema.List.WorkspaceId,
        schema.List.Name,
        schema.List.Description,
        schema.List.Kind,
        schema.List.AllowFolders,
        schema.ContentTypes.Select(ContentTypeEndpoints.ToResponse).ToList(),
        [new FieldDefinitionDto("title", "Title", "text", Required: true, MaxLength: ItemWriter.TitleMaxLength), .. schema.Fields.Values.Select(FieldDefinitionDto.From)],
        schema.List.CreatedAt,
        schema.List.UpdatedAt);
}
