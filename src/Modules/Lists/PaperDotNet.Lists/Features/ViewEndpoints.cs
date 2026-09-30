using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>A saved view; <c>layout</c> is <c>table</c>, <c>board</c>, <c>calendar</c> or <c>gallery</c>.</summary>
public sealed record ViewResponse(
    Guid Id, Guid ListId, string Name, IReadOnlyList<string> Columns, string? Filter, string? OrderBy, string? GroupBy, string Layout, bool IsDefault);

public sealed record ViewRequest(
    string? Name, IReadOnlyList<string>? Columns, string? Filter, string? OrderBy, string? GroupBy, string? Layout = null, bool IsDefault = false);

/// <summary>Saved views of a list (LST-09). Items are read through a view with <c>?viewId=</c>.</summary>
internal static class ViewEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"{ListEndpoints.Route}/{{listId:guid}}/views").WithTags("Views");
        group.MapGet("", ListAsync).RequireScope(ListScopes.Read).WithName("ListViews");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.Read).WithName("CreateView");
        group.MapPut("/{viewId:guid}", ReplaceAsync).RequireScope(ListScopes.Read).WithName("ReplaceView");
        group.MapDelete("/{viewId:guid}", DeleteAsync).RequireScope(ListScopes.Read).WithName("DeleteView");
    }

    private static async Task<Results<Ok<List<ViewResponse>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, Guid listId, Caller caller, ListSchemaLoader loader, ListsDbContext db, CancellationToken cancellationToken)
    {
        if (await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var context = db;
        var tenant = caller.TenantId;
        var list = listId;
        var ct = cancellationToken;
        var views = await context.Views.AsNoTracking().Where(v => v.TenantId == tenant && v.ListId == list).OrderBy(v => v.Name).ToListAsync(ct);
        return TypedResults.Ok(views.Select(ToResponse).ToList());
    }

    private static async Task<Results<Created<ViewResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, Guid listId, ViewRequest request, Caller caller, ListSchemaLoader loader, ItemQueryRunner runner, ListsDbContext db,
        CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema.Access.ListLevel < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (Validate(schema, request, caller, runner) is { } invalid)
        {
            return invalid;
        }

        var view = new ListView { Id = Ids.New(), TenantId = caller.TenantId, ListId = listId };
        Apply(view, request);
        await ClearOtherDefaultsAsync(db, view, cancellationToken);
        db.Views.Add(view);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v1.0/workspaces/{workspaceId}/lists/{listId}/views/{view.Id}", ToResponse(view));
    }

    private static async Task<Results<Ok<ViewResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid workspaceId, Guid listId, Guid viewId, ViewRequest request, Caller caller, ListSchemaLoader loader, ItemQueryRunner runner, ListsDbContext db,
        CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken);
        var view = schema is null ? null : await FindAsync(db, caller.TenantId, listId, viewId, tracking: true, cancellationToken);
        if (view is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema!.Access.ListLevel < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        if (Validate(schema, request, caller, runner) is { } invalid)
        {
            return invalid;
        }

        Apply(view, request);
        await ClearOtherDefaultsAsync(db, view, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(view));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid listId, Guid viewId, Caller caller, ListSchemaLoader loader, ListsDbContext db, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken);
        var view = schema is null ? null : await FindAsync(db, caller.TenantId, listId, viewId, tracking: true, cancellationToken);
        if (view is null)
        {
            return ApiErrors.NotFound();
        }

        if (schema!.Access.ListLevel < WorkspaceAccessLevel.Manage)
        {
            return ListEndpoints.Forbidden();
        }

        db.Views.Remove(view);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>A view of the list.</summary>
    internal static Task<ListView?> FindAsync(ListsDbContext database, Guid tenantId, Guid listId, Guid viewId, bool tracking, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var list = listId;
        var id = viewId;
        var ct = cancellationToken;
        return tracking
            ? context.Views.Where(v => v.TenantId == tenant && v.ListId == list && v.Id == id).FirstOrDefaultAsync(ct)
            : context.Views.AsNoTracking().Where(v => v.TenantId == tenant && v.ListId == list && v.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>The columns of a view.</summary>
    internal static IReadOnlyList<string> Columns(ListView view) => ListsJsonText.Strings(view.Columns);

    private static ValidationProblem? Validate(ListSchema schema, ViewRequest request, Caller caller, ItemQueryRunner runner)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
        {
            errors["name"] = ["A name of 1 to 200 characters is required."];
        }

        if (request.Filter is { Length: > 4000 } || request.OrderBy is { Length: > 1000 })
        {
            errors["query"] = ["The filter or order is too long."];
        }

        if (request.Layout is not null && !ViewLayouts.IsValid(request.Layout))
        {
            errors["layout"] = ["layout must be table, board, calendar or gallery."];
        }

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

        if (!errors.ContainsKey("query") && runner.Validate(schema, request.Filter, request.OrderBy, caller.UserId) is { } queryError)
        {
            errors["query"] = [queryError];
        }

        return errors.Count == 0 ? null : ApiErrors.Validation(errors);
    }

    private static void Apply(ListView view, ViewRequest request)
    {
        view.Name = request.Name!.Trim();
        view.Columns = JsonSerializer.Serialize(request.Columns ?? [], ListsJson.Default.IReadOnlyListString);
        view.Filter = string.IsNullOrWhiteSpace(request.Filter) ? null : request.Filter;
        view.OrderBy = string.IsNullOrWhiteSpace(request.OrderBy) ? null : request.OrderBy;
        view.GroupBy = request.GroupBy;
        view.Layout = request.Layout ?? ViewLayouts.Table;
        view.IsDefault = request.IsDefault;
    }

    private static async Task ClearOtherDefaultsAsync(ListsDbContext database, ListView view, CancellationToken cancellationToken)
    {
        if (!view.IsDefault)
        {
            return;
        }

        var context = database;
        var tenant = view.TenantId;
        var list = view.ListId;
        var id = view.Id;
        var ct = cancellationToken;
        foreach (var other in await context.Views.Where(v => v.TenantId == tenant && v.ListId == list && v.IsDefault && v.Id != id).ToListAsync(ct))
        {
            other.IsDefault = false;
        }
    }

    private static ViewResponse ToResponse(ListView v) => new(v.Id, v.ListId, v.Name, Columns(v), v.Filter, v.OrderBy, v.GroupBy, v.Layout, v.IsDefault);
}
