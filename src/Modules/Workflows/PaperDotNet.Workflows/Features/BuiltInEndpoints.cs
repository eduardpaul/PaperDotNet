using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>A built-in workflow in a workspace (or library): what it is, whether the server can run it, and its state there.</summary>
public sealed record BuiltInDto(
    string Key,
    string Name,
    string Description,
    JsonObject? Parameters,
    string? Requires,
    string Scope,
    bool Available,
    bool Enabled,
    JsonObject? Values,
    Guid? WorkflowId,
    [property: JsonPropertyName("@odata.etag"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ETag);

public sealed record SetBuiltInRequest(bool? Enabled, JsonObject? Parameters);

public sealed record CopyBuiltInRequest(string? Name, JsonObject? Parameters);

/// <summary>
/// The built-in workflows (EVT-12): the workspace's catalog (<c>…/workflows/builtIns</c>), turning one on with parameters
/// or off (<c>PUT</c>, with <c>If-Match</c> once it was turned on), copying one into a workflow of the workspace, and the
/// per-library ones (<c>…/lists/{listId}/workflows/builtIns</c>). Members read them, managers change them.
/// </summary>
internal static class BuiltInEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var workspace = app.MapGroup("/v1.0/workspaces/{workspaceId:guid}/workflows/builtIns").WithTags("Workflows");
        workspace.MapGet("", ListAsync).RequireScope(WorkflowScopes.Read).WithName("ListBuiltInWorkflows");
        workspace.MapPut("/{key}", SetAsync).RequireScope(WorkflowScopes.Write).WithName("SetBuiltInWorkflow");
        workspace.MapPost("/{key}/copy", CopyAsync).RequireScope(WorkflowScopes.Write).WithName("CopyBuiltInWorkflow");
        var library = app.MapGroup("/v1.0/workspaces/{workspaceId:guid}/lists/{listId:guid}/workflows/builtIns").WithTags("Workflows");
        library.MapGet("", ListOfLibraryAsync).RequireScope(WorkflowScopes.Read).WithName("ListLibraryBuiltInWorkflows");
        library.MapPut("/{key}", SetOfLibraryAsync).RequireScope(WorkflowScopes.Write).WithName("SetLibraryBuiltInWorkflow");
    }

    private static async Task<BuiltInDto> DtoAsync(BuiltInWorkflows builtIns, Guid tenantId, Guid workspaceId, BuiltInWorkflow workflow, ListData? list, CancellationToken ct)
    {
        var row = await builtIns.RowAsync(tenantId, workspaceId, workflow.Key, ct, list?.Id);
        return new BuiltInDto(
            workflow.Key, BuiltInWorkflows.RowName(workflow, list), workflow.Description, workflow.Parameters, workflow.Requires,
            workflow.Scope == BuiltInScope.Library ? "library" : "workspace", builtIns.IsAvailable(workflow),
            row?.Enabled ?? (workflow.EnabledByDefault && list is not null), BuiltInWorkflows.Values(row), row?.Id, row is null ? null : ETags.From(row.Version));
    }

    private static async Task<Results<Ok<List<BuiltInDto>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, Caller caller, IWorkspaceAccess workspaces, BuiltInWorkflows builtIns, CancellationToken cancellationToken)
    {
        if (await WorkflowEndpoints.CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Read, workspaces, cancellationToken) is { } denied)
        {
            return denied;
        }

        var result = new List<BuiltInDto>();
        foreach (var workflow in (await builtIns.ListAsync(caller.TenantId, cancellationToken)).Where(w => w.Scope == BuiltInScope.Workspace))
        {
            result.Add(await DtoAsync(builtIns, caller.TenantId, workspaceId, workflow, null, cancellationToken));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<List<BuiltInDto>>, ProblemHttpResult>> ListOfLibraryAsync(
        Guid workspaceId, Guid listId, Caller caller, IWorkspaceAccess workspaces, IListItemStore items, BuiltInWorkflows builtIns, CancellationToken cancellationToken)
    {
        if (await WorkflowEndpoints.CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Read, workspaces, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (await items.GetListAsync(workspaceId, listId, cancellationToken) is not { } list)
        {
            return ApiErrors.NotFound();
        }

        await builtIns.EnsureDefaultsAsync(caller.TenantId, list, cancellationToken);
        var result = new List<BuiltInDto>();
        foreach (var workflow in (await builtIns.ListAsync(caller.TenantId, cancellationToken)).Where(w => w.Scope == BuiltInScope.Library))
        {
            result.Add(await DtoAsync(builtIns, caller.TenantId, workspaceId, workflow, list, cancellationToken));
        }

        return TypedResults.Ok(result);
    }

    private static Task<Results<Ok<BuiltInDto>, ValidationProblem, ProblemHttpResult>> SetAsync(
        Guid workspaceId, string key, SetBuiltInRequest body, HttpContext http, Caller caller, IWorkspaceAccess workspaces, BuiltInWorkflows builtIns,
        WorkflowsDbContext db, CancellationToken cancellationToken) =>
        SetCoreAsync(workspaceId, null, key, body, http, caller, workspaces, null, builtIns, db, cancellationToken);

    private static Task<Results<Ok<BuiltInDto>, ValidationProblem, ProblemHttpResult>> SetOfLibraryAsync(
        Guid workspaceId, Guid listId, string key, SetBuiltInRequest body, HttpContext http, Caller caller, IWorkspaceAccess workspaces, IListItemStore items,
        BuiltInWorkflows builtIns, WorkflowsDbContext db, CancellationToken cancellationToken) =>
        SetCoreAsync(workspaceId, listId, key, body, http, caller, workspaces, items, builtIns, db, cancellationToken);

    private static async Task<Results<Ok<BuiltInDto>, ValidationProblem, ProblemHttpResult>> SetCoreAsync(
        Guid workspaceId, Guid? listId, string key, SetBuiltInRequest body, HttpContext http, Caller caller, IWorkspaceAccess workspaces, IListItemStore? items,
        BuiltInWorkflows builtIns, WorkflowsDbContext db, CancellationToken cancellationToken)
    {
        if (await WorkflowEndpoints.CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Manage, workspaces, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (await builtIns.FindAsync(caller.TenantId, key, cancellationToken) is not { } workflow)
        {
            return ApiErrors.NotFound();
        }

        ListData? list = null;
        if (listId is { } id && (list = await items!.GetListAsync(workspaceId, id, cancellationToken)) is null)
        {
            return ApiErrors.NotFound();
        }

        // Once it was turned on, changes need its ETag (from the catalog).
        if (await builtIns.RowAsync(caller.TenantId, workspaceId, key, cancellationToken, list?.Id) is { } existing)
        {
            if (!ETags.TryGetIfMatch(http.Request, out var etag))
            {
                return ApiErrors.PreconditionRequired();
            }

            if (existing.Version != etag)
            {
                return ApiErrors.PreconditionFailed();
            }
        }

        var (row, errors, nameTaken) = await builtIns.SetAsync(caller.TenantId, workspaceId, workflow, body.Enabled ?? true, body.Parameters, cancellationToken, list);
        if (nameTaken)
        {
            return ApiErrors.Conflict("nameTaken", errors[0]);
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["parameters"] = [.. errors] });
        }

        await db.SaveChangesAsync(cancellationToken);
        if (row is not null)
        {
            ETags.Set(http.Response, row.Version);
        }

        return TypedResults.Ok(await DtoAsync(builtIns, caller.TenantId, workspaceId, workflow, list, cancellationToken));
    }

    private static async Task<Results<Created<WorkflowDto>, ValidationProblem, ProblemHttpResult>> CopyAsync(
        Guid workspaceId, string key, CopyBuiltInRequest body, Caller caller, IWorkspaceAccess workspaces, BuiltInWorkflows builtIns, WorkflowsDbContext db,
        CancellationToken cancellationToken)
    {
        if (await WorkflowEndpoints.CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Manage, workspaces, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (await builtIns.FindAsync(caller.TenantId, key, cancellationToken) is not { Scope: BuiltInScope.Workspace } workflow)
        {
            return ApiErrors.NotFound();
        }

        var name = (body.Name ?? workflow.Name).Trim();
        if (name.Length is 0 or > 200)
        {
            return ApiErrors.Validation("name", "A name of 1 to 200 characters is required.");
        }

        var (copy, errors, nameTaken) = await builtIns.CopyAsync(caller.TenantId, workspaceId, workflow, name, body.Parameters, cancellationToken);
        if (nameTaken)
        {
            return ApiErrors.Conflict("nameTaken", errors[0]);
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["parameters"] = [.. errors] });
        }

        await db.SaveChangesAsync(cancellationToken);
        var version = (await WorkflowVersions.FindAsync(db, caller.TenantId, copy!.Id, copy.CurrentVersion, cancellationToken))!;
        return TypedResults.Created($"/v1.0/workspaces/{workspaceId}/workflows/{copy.Id}", WorkflowEndpoints.ToDto(copy, version));
    }
}
