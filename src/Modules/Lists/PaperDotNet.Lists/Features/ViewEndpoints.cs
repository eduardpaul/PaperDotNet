using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

public sealed record ViewResponse(
    Guid Id, Guid ListId, string Name, IReadOnlyList<string> Columns, string? Filter, string? OrderBy, string? GroupBy, ViewLayout Layout, bool IsDefault);

public sealed record ViewRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    IReadOnlyList<string>? Columns,
    [property: StringLength(4000)] string? Filter,
    [property: StringLength(1000)] string? OrderBy,
    string? GroupBy,
    ViewLayout Layout = ViewLayout.Table,
    bool IsDefault = false);

/// <summary>Saved views of a list (LST-09). Items are read through a view with <c>?viewId=</c>.</summary>
internal static class ViewEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group($"{ListEndpoints.Route}/{{listId:guid}}/views", "Views");
        group.MapGet("", ListAsync).RequireScope(ListScopes.Read).WithName("ListViews");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.Read).WithName("CreateView");
        group.MapPut("/{viewId:guid}", ReplaceAsync).RequireScope(ListScopes.Read).WithName("ReplaceView");
        group.MapDelete("/{viewId:guid}", DeleteAsync).RequireScope(ListScopes.Read).WithName("DeleteView");
    }

    private static async Task<Results<Ok<List<ViewResponse>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, Guid listId, ListSchemaLoader loader, ListsDbContext db, CancellationToken ct)
    {
        if (await loader.LoadAsync(workspaceId, listId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var views = await db.Views.AsNoTracking().Where(v => v.ListId == listId).OrderBy(v => v.Name).ToListAsync(ct);
        return TypedResults.Ok(views.Select(ToResponse).ToList());
    }

    private static async Task<Results<Created<ViewResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, Guid listId, ViewRequest request, ListSchemaLoader loader, ItemQueryRunner runner, ListsDbContext db, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Permission < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (Validate(schema, request, runner) is { } invalid)
        {
            return invalid;
        }

        var view = new ListView { Id = Ids.New(), ListId = listId, Name = request.Name.Trim() };
        Apply(view, request);
        await ClearOtherDefaultsAsync(db, listId, view, ct);
        db.Views.Add(view);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/lists/{listId}/views/{view.Id}", ToResponse(view));
    }

    private static async Task<Results<Ok<ViewResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid workspaceId, Guid listId, Guid viewId, ViewRequest request, ListSchemaLoader loader, ItemQueryRunner runner, ListsDbContext db, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        var view = schema is null ? null : await db.Views.FirstOrDefaultAsync(v => v.Id == viewId && v.ListId == listId, ct);
        if (view is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema!.Permission < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (Validate(schema, request, runner) is { } invalid)
        {
            return invalid;
        }

        view.Name = request.Name.Trim();
        Apply(view, request);
        await ClearOtherDefaultsAsync(db, listId, view, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToResponse(view));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid listId, Guid viewId, ListSchemaLoader loader, ListsDbContext db, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        var view = schema is null ? null : await db.Views.FirstOrDefaultAsync(v => v.Id == viewId && v.ListId == listId, ct);
        if (view is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema!.Permission < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        db.Views.Remove(view);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static ValidationProblem? Validate(ListSchema schema, ViewRequest request, ItemQueryRunner runner)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var errors = new Dictionary<string, string[]>();
        var known = schema.Fields.Keys.Append("title").ToHashSet(StringComparer.Ordinal);
        var unknown = (request.Columns ?? []).Where(c => !known.Contains(c)).ToList();
        if (unknown.Count > 0)
        {
            errors["columns"] = [$"Unknown fields: {string.Join(", ", unknown)}."];
        }

        if (request.GroupBy is not null && !known.Contains(request.GroupBy))
        {
            errors["groupBy"] = ["Unknown field."];
        }

        if (runner.Validate(schema, request.Filter, request.OrderBy) is { } queryError)
        {
            errors["query"] = [queryError];
        }

        return errors.Count == 0 ? null : ApiErrors.Validation(errors);
    }

    private static void Apply(ListView view, ViewRequest request)
    {
        view.Columns = request.Columns?.ToList() ?? [];
        view.Filter = request.Filter;
        view.OrderBy = request.OrderBy;
        view.GroupBy = request.GroupBy;
        view.Layout = request.Layout;
        view.IsDefault = request.IsDefault;
    }

    private static async Task ClearOtherDefaultsAsync(ListsDbContext db, Guid listId, ListView view, CancellationToken ct)
    {
        if (view.IsDefault)
        {
            foreach (var other in await db.Views.Where(v => v.ListId == listId && v.IsDefault && v.Id != view.Id).ToListAsync(ct))
            {
                other.IsDefault = false;
            }
        }
    }

    private static ViewResponse ToResponse(ListView v) => new(v.Id, v.ListId, v.Name, v.Columns, v.Filter, v.OrderBy, v.GroupBy, v.Layout, v.IsDefault);
}
