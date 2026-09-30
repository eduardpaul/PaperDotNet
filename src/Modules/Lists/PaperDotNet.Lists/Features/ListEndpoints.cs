using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Messaging;

namespace PaperDotNet.Lists.Features;

public sealed record ListDto(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<FieldDefinition> Fields,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedBy,
    [property: JsonPropertyName("@odata.etag")] string ETag);

public sealed record CreateListRequest(string Name, string? Description, IReadOnlyList<FieldDefinition>? Fields);

public sealed record UpdateListRequest(string? Name, string? Description, IReadOnlyList<FieldDefinition>? Fields);

/// <summary>Lists: create, read, change and delete. Queries copy their arguments into locals (ADR-0039).</summary>
internal static class ListEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/lists").WithTags("Lists");
        group.MapGet("/", ListAsync).RequireScope(ListScopes.Read).WithName("ListLists");
        group.MapPost("/", CreateAsync).RequireScope(ListScopes.Write).WithName("CreateList");
        group.MapGet("/{listId:guid}", GetAsync).RequireScope(ListScopes.Read).WithName("GetList");
        group.MapPatch("/{listId:guid}", UpdateAsync).RequireScope(ListScopes.Write).WithName("UpdateList");
        group.MapDelete("/{listId:guid}", DeleteAsync).RequireScope(ListScopes.Write).WithName("DeleteList");
    }

    public static ListDto ToDto(ListDefinition list) =>
        new(list.Id, list.Name, list.Description, ListItemStore.FieldsOf(list), list.CreatedAt, list.CreatedBy, list.UpdatedAt, list.UpdatedBy, ETags.From(list.Version));

    public static Task<ListDefinition?> FindAsync(ListsDbContext database, Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = listId;
        var ct = cancellationToken;
        return db.Lists.Where(l => l.TenantId == tenant && l.Id == id).FirstOrDefaultAsync(ct);
    }

    private static Task<bool> NameTakenAsync(ListsDbContext database, Guid tenantId, string listName, Guid exceptId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var name = listName;
        var except = exceptId;
        var ct = cancellationToken;
        return db.Lists.AnyAsync(l => l.TenantId == tenant && l.Name == name && l.Id != except, ct);
    }

    private static async Task<Ok<Page<ListDto>>> ListAsync(HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller, ListsDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        List<ListDefinition> lists;
        if (page.After is { } after)
        {
            lists = await db.Lists.Where(l => l.TenantId == tenant && l.Id.CompareTo(after) > 0).OrderBy(l => l.Id).Take(take).ToListAsync(ct);
        }
        else
        {
            lists = await db.Lists.Where(l => l.TenantId == tenant).OrderBy(l => l.Id).Take(take).ToListAsync(ct);
        }

        return TypedResults.Ok(Page.Create([.. lists.Select(ToDto)], page, request, l => l.Id));
    }

    private static async Task<Results<Created<ListDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateListRequest body, Caller caller, ListsDbContext db, IOutbox outbox, CancellationToken cancellationToken)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is 0 or > 255)
        {
            return ApiErrors.Validation("name", "A name of 1 to 255 characters is required.");
        }

        var fields = body.Fields ?? [];
        if (FieldValues.Validate(fields) is { Count: > 0 } errors)
        {
            return ApiErrors.Validation(errors);
        }

        if (await NameTakenAsync(db, caller.TenantId, name, Guid.Empty, cancellationToken))
        {
            return ApiErrors.Conflict("nameTaken", "A list with this name exists.");
        }

        var list = new ListDefinition
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            Name = name,
            Description = body.Description,
            Fields = JsonSerializer.Serialize(fields, ListsJson.Default.IReadOnlyListFieldDefinition),
        };
        db.Lists.Add(list);
        await outbox.SaveChangesAsync(db, [new ListCreated(list.Id, list.Name) { TenantId = caller.TenantId, UserId = caller.UserId }], cancellationToken);
        return TypedResults.Created($"/v1.0/lists/{list.Id}", ToDto(list));
    }

    private static async Task<Results<Ok<ListDto>, ProblemHttpResult>> GetAsync(Guid listId, HttpResponse response, Caller caller, ListsDbContext db, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, caller.TenantId, listId, cancellationToken) is not { } list)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, list.Version);
        return TypedResults.Ok(ToDto(list));
    }

    private static async Task<Results<Ok<ListDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid listId, UpdateListRequest body, HttpContext http, Caller caller, ListsDbContext db, CancellationToken cancellationToken)
    {
        if (!ETags.TryGetIfMatch(http.Request, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (await FindAsync(db, caller.TenantId, listId, cancellationToken) is not { } list)
        {
            return ApiErrors.NotFound();
        }

        if (list.Version != version)
        {
            return ApiErrors.PreconditionFailed();
        }

        if (body.Name is not null)
        {
            var name = body.Name.Trim();
            if (name.Length is 0 or > 255)
            {
                return ApiErrors.Validation("name", "A name of 1 to 255 characters is required.");
            }

            if (await NameTakenAsync(db, caller.TenantId, name, list.Id, cancellationToken))
            {
                return ApiErrors.Conflict("nameTaken", "A list with this name exists.");
            }

            list.Name = name;
        }

        if (body.Description is not null)
        {
            list.Description = body.Description.Length == 0 ? null : body.Description;
        }

        if (body.Fields is { } fields)
        {
            if (FieldValues.Validate(fields) is { Count: > 0 } errors)
            {
                return ApiErrors.Validation(errors);
            }

            // Stored values keep their form: a field keeps its type once created.
            var current = ListItemStore.FieldsOf(list).ToDictionary(f => f.Name, StringComparer.Ordinal);
            if (fields.FirstOrDefault(f => current.TryGetValue(f.Name, out var old) && old.Type != f.Type) is { } changed)
            {
                return ApiErrors.Validation("fields", $"The type of the field '{changed.Name}' cannot change.");
            }

            list.Fields = JsonSerializer.Serialize(fields, ListsJson.Default.IReadOnlyListFieldDefinition);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(http.Response, list.Version);
        return TypedResults.Ok(ToDto(list));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid listId, HttpRequest request, Caller caller, ListsDbContext db, IOutbox outbox, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, caller.TenantId, listId, cancellationToken) is not { } list)
        {
            return ApiErrors.NotFound();
        }

        if (ETags.HasIfMatch(request) && (!ETags.TryGetIfMatch(request, out var version) || version != list.Version))
        {
            return ApiErrors.PreconditionFailed();
        }

        // Items go with the list (foreign key cascade).
        db.Lists.Remove(list);
        await outbox.SaveChangesAsync(db, [new ListDeleted(list.Id, list.Name) { TenantId = caller.TenantId, UserId = caller.UserId }], cancellationToken);
        return TypedResults.NoContent();
    }
}
