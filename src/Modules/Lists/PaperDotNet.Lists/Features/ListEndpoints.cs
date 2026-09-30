using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Messaging;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

public sealed record ListSummary(Guid Id, Guid WorkspaceId, string Name, string? Description, string Kind, bool AllowFolders, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? TemplateKey);

/// <summary>A list with its content types and effective columns.</summary>
public sealed record ListResponse(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string? Description,
    string Kind,
    bool AllowFolders,
    string Versioning,
    int MaxVersions,
    string? TemplateKey,
    IReadOnlyList<ContentTypeResponse> ContentTypes,
    IReadOnlyList<FieldDefinitionDto> Columns,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("@odata.etag")] string ETag);

/// <summary>A new list: <c>kind</c> is <c>list</c> (default) or <c>library</c>; <c>versioning</c> <c>off</c> or <c>major</c>.</summary>
public sealed record CreateListRequest(
    string? Name,
    string? Description,
    string? Kind = null,
    bool AllowFolders = true,
    IReadOnlyList<Guid>? ContentTypeIds = null,
    string? Versioning = null,
    int MaxVersions = ListDefinition.DefaultMaxVersions,
    string? TemplateKey = null);

public sealed record UpdateListRequest(string? Name, string? Description, bool? AllowFolders, string? Versioning = null, int? MaxVersions = null);

public sealed record AddListContentTypeRequest(Guid ContentTypeId);

/// <summary>Lists and libraries of a workspace. Workspace owners (and administrators) create and change them.</summary>
internal static class ListEndpoints
{
    public const string Route = "/v1.0/workspaces/{workspaceId:guid}/lists";

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Lists");
        group.MapGet("", ListAsync).RequireScope(ListScopes.Read).WithName("ListLists");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.Read).WithName("CreateList");
        group.MapGet("/{listId:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetList");
        group.MapPatch("/{listId:guid}", UpdateAsync).RequireScope(ListScopes.Read).WithName("UpdateList");
        group.MapDelete("/{listId:guid}", DeleteAsync).RequireScope(ListScopes.Read).WithName("DeleteList");
        group.MapPost("/{listId:guid}/contentTypes", AddContentTypeAsync).RequireScope(ListScopes.Read).WithName("AddListContentType");
        group.MapDelete("/{listId:guid}/contentTypes/{contentTypeId:guid}", RemoveContentTypeAsync).RequireScope(ListScopes.Read).WithName("RemoveListContentType");
    }

    internal static ListCaller CallerOf(Caller caller) => new(caller.TenantId, caller.UserId);

    private static async Task<Results<Ok<List<ListSummary>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, Caller caller, IWorkspaceAccess workspaces, ListSchemaLoader loader, CancellationToken cancellationToken)
    {
        var level = await workspaces.GetPermissionAsync(caller.TenantId, caller.UserId, workspaceId, cancellationToken);
        if (level == WorkspaceAccessLevel.None)
        {
            return ApiErrors.NotFound();
        }

        var lists = await loader.VisibleListsAsync(CallerOf(caller), workspaceId, level, cancellationToken);
        return TypedResults.Ok(lists.Select(l => new ListSummary(l.Id, l.WorkspaceId, l.Name, l.Description, l.Kind, l.AllowFolders, l.CreatedAt, l.UpdatedAt, l.TemplateKey)).ToList());
    }

    /// <summary>Creates a list from content types (the built-in Item content type when none is given).</summary>
    private static async Task<Results<Created<ListResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, CreateListRequest request, Caller caller, IWorkspaceAccess workspaces, ListsDbContext db, ListSchemaLoader loader,
        IOutbox outbox, HttpResponse response, CancellationToken cancellationToken)
    {
        var permission = await workspaces.GetPermissionAsync(caller.TenantId, caller.UserId, workspaceId, cancellationToken);
        if (permission == WorkspaceAccessLevel.None)
        {
            return ApiErrors.NotFound();
        }

        if (permission < WorkspaceAccessLevel.Manage)
        {
            return Forbidden();
        }

        if (Validate(request.Name, request.Description, request.Versioning, request.MaxVersions, nameRequired: true) is { } invalid)
        {
            return invalid;
        }

        if (request.Kind is not null && !ListKinds.IsValid(request.Kind))
        {
            return ApiErrors.Validation("kind", "The kind is list or library.");
        }

        if (request.TemplateKey is not null)
        {
            return ApiErrors.Validation("templateKey", "Unknown list template.");
        }

        var contentTypeIds = request.ContentTypeIds?.Distinct().ToList() ?? [];
        if (contentTypeIds.Count == 0)
        {
            contentTypeIds.Add(await ContentTypeEndpoints.EnsureItemContentTypeAsync(db, caller.TenantId, cancellationToken));
        }

        var contentTypes = await loader.ContentTypesAsync(caller.TenantId, contentTypeIds, cancellationToken);
        if (contentTypes.Count != contentTypeIds.Count)
        {
            return ApiErrors.Validation("contentTypeIds", "Unknown content type.");
        }

        var fields = new List<FieldDefinition>();
        foreach (var contentType in contentTypes)
        {
            if (ListSchema.FindConflict(fields, contentType.Fields) is { } conflict)
            {
                return ApiErrors.Conflict("fieldConflict", conflict);
            }

            fields.AddRange(contentType.Fields);
        }

        var kind = request.Kind ?? ListKinds.List;
        var list = new ListDefinition
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            WorkspaceId = workspaceId,
            Name = request.Name!.Trim(),
            Description = request.Description,
            Kind = kind,
            AllowFolders = request.AllowFolders,
            ContentTypeIds = ListsJsonText.Ids(contentTypeIds),
            MaxVersions = request.MaxVersions,

            // Libraries keep versions by default (like SharePoint document libraries).
            Versioning = request.Versioning ?? (kind == ListKinds.Library ? ListVersionings.Major : ListVersionings.Off),
        };
        db.Lists.Add(list);
        await outbox.SaveChangesAsync(db, [new ListCreated { TenantId = caller.TenantId, UserId = caller.UserId, WorkspaceId = workspaceId, ListId = list.Id, Name = list.Name }], cancellationToken);
        ETags.Set(response, list.Version);
        return TypedResults.Created($"/v1.0/workspaces/{workspaceId}/lists/{list.Id}", ToResponse(new ListSchema(list, contentTypes, ListAccess.Full(list.Id))));
    }

    private static async Task<Results<Ok<ListResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid listId, Caller caller, ListSchemaLoader loader, HttpResponse response, CancellationToken cancellationToken)
    {
        if (await loader.LoadAsync(CallerOf(caller), workspaceId, listId, cancellationToken) is not { } schema)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, schema.List.Version);
        return TypedResults.Ok(ToResponse(schema));
    }

    private static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid workspaceId, Guid listId, UpdateListRequest request, Caller caller, ListSchemaLoader loader, ListsDbContext db,
        HttpRequest http, HttpResponse response, CancellationToken cancellationToken)
    {
        if (Validate(request.Name, request.Description, request.Versioning, request.MaxVersions, nameRequired: false) is { } invalid)
        {
            return invalid;
        }

        var (schema, problem) = await LoadForChangeAsync(caller, workspaceId, listId, loader, http, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var list = schema!.List;
        list.Name = request.Name?.Trim() ?? list.Name;
        list.Description = request.Description ?? list.Description;
        list.AllowFolders = request.AllowFolders ?? list.AllowFolders;
        list.Versioning = request.Versioning ?? list.Versioning;
        list.MaxVersions = request.MaxVersions ?? list.MaxVersions;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(response, list.Version);
        return TypedResults.Ok(ToResponse(schema));
    }

    /// <summary>Moves the list to the recycle bin; system lists stay.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid listId, Caller caller, ListSchemaLoader loader, ListsDbContext db, IOutbox outbox, HttpRequest http, CancellationToken cancellationToken)
    {
        var (schema, problem) = await LoadForChangeAsync(caller, workspaceId, listId, loader, http, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (schema!.List.SystemKey is not null)
        {
            return ApiErrors.Conflict("systemList", "This list is part of the system and cannot be deleted.");
        }

        db.Lists.Remove(schema.List);
        try
        {
            await outbox.SaveChangesAsync(db, [new ListDeleted { TenantId = caller.TenantId, UserId = caller.UserId, WorkspaceId = workspaceId, ListId = listId, Name = schema.List.Name }], cancellationToken);
            return TypedResults.NoContent();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }
    }

    private static async Task<Results<Ok<ListResponse>, ProblemHttpResult>> AddContentTypeAsync(
        Guid workspaceId, Guid listId, AddListContentTypeRequest request, Caller caller, ListSchemaLoader loader, ListsDbContext db,
        HttpResponse response, CancellationToken cancellationToken)
    {
        var listCaller = CallerOf(caller);
        if (await loader.LoadAsync(listCaller, workspaceId, listId, cancellationToken, tracking: true) is not { } schema)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Manage)
        {
            return Forbidden();
        }

        if (await loader.FindContentTypeAsync(caller.TenantId, request.ContentTypeId, cancellationToken) is not { } contentType)
        {
            return ApiErrors.NotFound("The content type was not found.");
        }

        if (!schema.ContentTypeIds.Contains(contentType.Id))
        {
            if (ListSchema.FindConflict(schema.Fields.Values, ListsJsonText.Fields(contentType.Fields)) is { } conflict)
            {
                return ApiErrors.Conflict("fieldConflict", conflict);
            }

            schema.List.ContentTypeIds = ListsJsonText.Ids([.. schema.ContentTypeIds, contentType.Id]);
            await db.SaveChangesAsync(cancellationToken);
        }

        var updated = await loader.LoadAsync(listCaller, workspaceId, listId, cancellationToken);
        ETags.Set(response, updated!.List.Version);
        return TypedResults.Ok(ToResponse(updated));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveContentTypeAsync(
        Guid workspaceId, Guid listId, Guid contentTypeId, Caller caller, ListSchemaLoader loader, ListsDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var schema = await loader.LoadAsync(CallerOf(caller), workspaceId, listId, cancellationToken, tracking: true);
        if (schema is null || !schema.ContentTypeIds.Contains(contentTypeId))
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Manage)
        {
            return Forbidden();
        }

        if (schema.ContentTypeIds.Count == 1)
        {
            return ApiErrors.Conflict("lastContentType", "A list needs at least one content type.");
        }

        var tenant = caller.TenantId;
        var list = listId;
        var type = contentTypeId;
        var ct = cancellationToken;
        if (await db.Items.AnyAsync(i => i.TenantId == tenant && i.ListId == list && i.ContentTypeId == type && i.DeletedAt == null, ct))
        {
            return ApiErrors.Conflict("contentTypeInUse", "Items in this list still use the content type.");
        }

        schema.List.ContentTypeIds = ListsJsonText.Ids(schema.ContentTypeIds.Where(id => id != contentTypeId));
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Loads a list the caller may restructure, enforcing <c>If-Match</c>.</summary>
    private static async Task<(ListSchema? Schema, ProblemHttpResult? Problem)> LoadForChangeAsync(
        Caller caller, Guid workspaceId, Guid listId, ListSchemaLoader loader, HttpRequest http, CancellationToken cancellationToken)
    {
        if (await loader.LoadAsync(CallerOf(caller), workspaceId, listId, cancellationToken, tracking: true) is not { } schema)
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

        return version != schema.List.Version ? (null, ApiErrors.PreconditionFailed()) : (schema, null);
    }

    private static ValidationProblem? Validate(string? name, string? description, string? versioning, int? maxVersions, bool nameRequired) =>
        (nameRequired || name is not null) && name?.Trim() is not { Length: > 0 and <= 200 } ? ApiErrors.Validation("name", "A name of 1 to 200 characters is required.")
        : description is { Length: > 2000 } ? ApiErrors.Validation("description", "The description can have at most 2000 characters.")
        : versioning is not null && !ListVersionings.IsValid(versioning) ? ApiErrors.Validation("versioning", "Versioning is off or major.")
        : maxVersions is < 1 or > ListDefinition.MaxVersionsLimit ? ApiErrors.Validation("maxVersions", $"Between 1 and {ListDefinition.MaxVersionsLimit} versions are kept.")
        : null;

    internal static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.");

    private static ListResponse ToResponse(ListSchema schema) => new(
        schema.List.Id,
        schema.List.WorkspaceId,
        schema.List.Name,
        schema.List.Description,
        schema.List.Kind,
        schema.List.AllowFolders,
        schema.List.Versioning,
        schema.List.MaxVersions,
        schema.List.TemplateKey,
        [.. schema.ContentTypes.Select(c => ContentTypeEndpoints.ToResponse(c.Entity))],
        [new FieldDefinitionDto("title", "Title", "text", Required: true, MaxLength: ItemWriter.TitleMaxLength), .. schema.Fields.Values.Select(FieldDefinitionDto.From)],
        schema.List.CreatedAt,
        schema.List.UpdatedAt,
        ETags.From(schema.List.Version));
}
