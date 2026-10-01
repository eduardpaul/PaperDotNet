using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Workflows.Features;

public sealed record WorkflowDto(
    Guid Id,
    string Name,
    string Key,
    string? Description,
    bool Enabled,
    int Version,
    JsonObject Definition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BuiltIn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CopiedFrom,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("@odata.etag")] string ETag);

public sealed record CreateWorkflowRequest(string Name, string? Description, bool? Enabled, JsonObject Definition);

public sealed record UpdateWorkflowRequest(string? Name, string? Description, bool? Enabled, JsonObject? Definition);

/// <summary>A manual start: on an item (<c>itemId</c>), on several (<c>itemIds</c>: one run each) or without an item, with <c>inputs</c>.</summary>
public sealed record StartRunRequest(Guid? ListId, Guid? ItemId, JsonObject? Inputs, IReadOnlyList<Guid>? ItemIds = null);

public sealed record TriggerDto(string Key, string Description);

public sealed record RunDto(
    Guid Id,
    Guid WorkflowId,
    int WorkflowVersion,
    string Status,
    string Trigger,
    string? Node,
    Guid? ListId,
    Guid? ItemId,
    JsonNode? Outputs,
    JsonNode? Variables,
    JsonNode? Log,
    string? Error,
    string? FailedNode,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record ActivityDto(string Key, string Description, IReadOnlyList<string> Outcomes);

/// <summary>
/// Workflows (ADR-0036) of a workspace: members read them, owners (workspace managers) change them, contributors start
/// them. Queries copy their arguments into locals (ADR-0039).
/// </summary>
internal static class WorkflowEndpoints
{
    private const string Route = "/v1.0/workspaces/{workspaceId:guid}/workflows";

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Workflows");
        group.MapGet("", ListAsync).RequireScope(WorkflowScopes.Read).WithName("ListWorkflows");
        group.MapPost("", CreateAsync).RequireScope(WorkflowScopes.Write).WithName("CreateWorkflow");
        group.MapGet("/{workflowId:guid}", GetAsync).RequireScope(WorkflowScopes.Read).WithName("GetWorkflow");
        group.MapPatch("/{workflowId:guid}", UpdateAsync).RequireScope(WorkflowScopes.Write).WithName("UpdateWorkflow");
        group.MapDelete("/{workflowId:guid}", DeleteAsync).RequireScope(WorkflowScopes.Write).WithName("DeleteWorkflow");
        group.MapPost("/{workflowId:guid}/runs", StartAsync).RequireScope(WorkflowScopes.Write).WithName("StartWorkflowRun");
        group.MapGet("/{workflowId:guid}/runs", ListRunsAsync).RequireScope(WorkflowScopes.Read).WithName("ListWorkflowRuns");
        group.MapGet("/runs/{runId:guid}", GetRunAsync).RequireScope(WorkflowScopes.Read).WithName("GetWorkflowRun");
        group.MapPost("/runs/{runId:guid}/cancel", CancelRunAsync).RequireScope(WorkflowScopes.Write).WithName("CancelWorkflowRun")
            .WithDescription("Stops a running or waiting run; its pending approvals are cancelled.");
        group.MapPost("/runs/{runId:guid}/retry", RetryRunAsync).RequireScope(WorkflowScopes.Write).WithName("RetryWorkflowRun")
            .WithDescription("Runs a failed run again from the node where it failed.");
        app.MapGet("/v1.0/workflows/triggers", (TriggerCatalog catalog) => TypedResults.Ok<IReadOnlyList<TriggerDto>>([.. catalog.All.Select(t => new TriggerDto(t.Key, t.Description))]))
            .RequireScope(WorkflowScopes.Read).WithTags("Workflows").WithName("ListWorkflowTriggers");
        app.MapGet("/v1.0/workflows/activities", Activities).RequireScope(WorkflowScopes.Read).WithTags("Workflows").WithName("ListWorkflowActivities");
    }

    /// <summary>The caller's access to the workspace, or a problem: 404 when it is not visible, 403 below <paramref name="needed"/>.</summary>
    internal static async Task<ProblemHttpResult?> CheckAsync(Caller caller, Guid workspaceId, WorkspaceAccessLevel needed, IWorkspaceAccess workspaces, CancellationToken cancellationToken)
    {
        var level = await workspaces.GetPermissionAsync(caller.TenantId, caller.UserId, workspaceId, cancellationToken);
        return level == WorkspaceAccessLevel.None ? ApiErrors.NotFound()
            : level < needed ? ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.")
            : null;
    }

    internal static WorkflowDto ToDto(WorkflowDefinition workflow, WorkflowVersion version) =>
        new(workflow.Id, workflow.Name, workflow.EventKey, workflow.Description, workflow.Enabled, workflow.CurrentVersion, JsonNode.Parse(version.Definition)!.AsObject(),
            workflow.BuiltInKey, workflow.CopiedFrom, workflow.CreatedAt, workflow.UpdatedAt, ETags.From(workflow.Version));

    private static RunDto ToDto(WorkflowRun run) =>
        new(run.Id, run.WorkflowId, run.WorkflowVersion, run.Status, run.Trigger, run.Node, run.ListId, run.ItemId,
            JsonNode.Parse(run.Outputs), JsonNode.Parse(run.Variables), JsonNode.Parse(run.Log), run.Error, run.FailedNode, run.StartedAt, run.CompletedAt);

    private static Task<WorkflowDefinition?> FindAsync(WorkflowsDbContext database, Guid tenantId, Guid workspaceId, Guid workflowId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var workspace = workspaceId;
        var id = workflowId;
        var ct = cancellationToken;
        return db.Workflows.Where(w => w.TenantId == tenant && w.WorkspaceId == workspace && w.Id == id).FirstOrDefaultAsync(ct);
    }

    internal static Task<bool> NameTakenAsync(WorkflowsDbContext database, Guid tenantId, Guid workspaceId, string workflowName, Guid exceptId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var workspace = workspaceId;
        var name = workflowName;
        var except = exceptId;
        var ct = cancellationToken;
        return db.Workflows.AnyAsync(w => w.TenantId == tenant && w.WorkspaceId == workspace && w.Name == name && w.Id != except, ct);
    }

    /// <summary>The definition checked and normalized, or the validation problem.</summary>
    private static async Task<(WorkflowSpec? Spec, ValidationProblem? Problem)> CheckDefinitionAsync(
        JsonObject? definition, IEnumerable<IWorkflowActivity> activities, TriggerCatalog catalog, TriggerTerms terms, Guid tenantId, CancellationToken cancellationToken)
    {
        if (definition is null)
        {
            return (null, ApiErrors.Validation("definition", "A definition is required."));
        }

        var (spec, error) = WorkflowJson.Read(definition);
        if (spec is null)
        {
            return (null, ApiErrors.Validation("definition", error ?? "Not a workflow definition."));
        }

        var errors = DefinitionValidator.Validate(spec, activities.ToDictionary(a => a.Key, StringComparer.Ordinal), catalog);
        if (errors.Count == 0)
        {
            errors.AddRange(await terms.CheckAsync(tenantId, spec, cancellationToken));
        }

        return errors.Count > 0 ? (null, ApiErrors.Validation(new Dictionary<string, string[]> { ["definition"] = [.. errors] })) : (spec, null);
    }

    internal static string TriggerTypes(WorkflowSpec spec) => $",{string.Join(',', spec.AllTriggers.Select(t => t.Type).Distinct(StringComparer.Ordinal))},";

    private static Ok<IReadOnlyList<ActivityDto>> Activities(IEnumerable<IWorkflowActivity> activities) =>
        TypedResults.Ok<IReadOnlyList<ActivityDto>>([
            .. FlowActivities.All.Select(a => new ActivityDto(a, "Flow activity (see ADR-0036).", [.. FlowActivities.Ports(a, null)])),
            .. activities.Select(a => new ActivityDto(a.Key, a.Description, ["done", "error", .. a.Outcomes])),
        ]);

    private static async Task<Results<Ok<Page<WorkflowDto>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller,
        IWorkspaceAccess workspaces, WorkflowsDbContext database, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Read, workspaces, cancellationToken) is { } problem)
        {
            return problem;
        }

        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var workspace = workspaceId;
        var after = page.After ?? Guid.Empty;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var workflows = await db.Workflows.Where(w => w.TenantId == tenant && w.WorkspaceId == workspace && w.Id.CompareTo(after) > 0).OrderBy(w => w.Id).Take(take).ToListAsync(ct);
        var dtos = new List<WorkflowDto>();
        foreach (var workflow in workflows)
        {
            dtos.Add(ToDto(workflow, (await WorkflowVersions.FindAsync(db, tenant, workflow.Id, workflow.CurrentVersion, ct))!));
        }

        return TypedResults.Ok(Page.Create(dtos, page, request, w => w.Id));
    }

    private static async Task<Results<Created<WorkflowDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, CreateWorkflowRequest body, Caller caller, IWorkspaceAccess workspaces, WorkflowsDbContext db, IEnumerable<IWorkflowActivity> activities,
        TriggerCatalog catalog, TriggerTerms terms, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Manage, workspaces, cancellationToken) is { } denied)
        {
            return denied;
        }

        var name = body.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200)
        {
            return ApiErrors.Validation("name", "A name of 1 to 200 characters is required.");
        }

        var (spec, problem) = await CheckDefinitionAsync(body.Definition, activities, catalog, terms, caller.TenantId, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (await NameTakenAsync(db, caller.TenantId, workspaceId, name, Guid.Empty, cancellationToken))
        {
            return ApiErrors.Conflict("nameTaken", "A workflow with this name exists.");
        }

        var workflow = new WorkflowDefinition
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            WorkspaceId = workspaceId,
            Name = name,
            Key = await UniqueKeyAsync(db, caller.TenantId, workspaceId, name, cancellationToken),
            Description = body.Description,
            Enabled = body.Enabled ?? true,
            CurrentVersion = 1,
            TriggerTypes = TriggerTypes(spec!),
        };
        var version = new WorkflowVersion { Id = Ids.New(), TenantId = caller.TenantId, WorkflowId = workflow.Id, Number = 1, Definition = WorkflowJson.Serialize(spec!), CreatedAt = time.GetUtcNow() };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v1.0/workspaces/{workspaceId}/workflows/{workflow.Id}", ToDto(workflow, version));
    }

    private static async Task<Results<Ok<WorkflowDto>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid workflowId, HttpResponse response, Caller caller, IWorkspaceAccess workspaces, WorkflowsDbContext db, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Read, workspaces, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (await FindAsync(db, caller.TenantId, workspaceId, workflowId, cancellationToken) is not { } workflow
            || await WorkflowVersions.FindAsync(db, caller.TenantId, workflowId, workflow.CurrentVersion, cancellationToken) is not { } version)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, workflow.Version);
        return TypedResults.Ok(ToDto(workflow, version));
    }

    private static async Task<Results<Ok<WorkflowDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid workspaceId, Guid workflowId, UpdateWorkflowRequest body, HttpContext http, Caller caller, IWorkspaceAccess workspaces, WorkflowsDbContext db,
        IEnumerable<IWorkflowActivity> activities, TriggerCatalog catalog, TriggerTerms terms, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Manage, workspaces, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!ETags.TryGetIfMatch(http.Request, out var etag))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (await FindAsync(db, caller.TenantId, workspaceId, workflowId, cancellationToken) is not { } workflow)
        {
            return ApiErrors.NotFound();
        }

        if (workflow.Version != etag)
        {
            return ApiErrors.PreconditionFailed();
        }

        if (workflow.BuiltInKey is { } builtIn && (body.Definition is not null || body.Name is not null))
        {
            return ApiErrors.Conflict("builtIn", $"The built-in workflow '{builtIn}' cannot be changed: set its parameters, or copy it (…/workflows/builtIns/{builtIn}/copy).");
        }

        if (body.Name is { } newName)
        {
            var name = newName.Trim();
            if (name.Length is 0 or > 200)
            {
                return ApiErrors.Validation("name", "A name of 1 to 200 characters is required.");
            }

            if (await NameTakenAsync(db, caller.TenantId, workspaceId, name, workflow.Id, cancellationToken))
            {
                return ApiErrors.Conflict("nameTaken", "A workflow with this name exists.");
            }

            workflow.Name = name;
        }

        workflow.Description = body.Description ?? workflow.Description;
        workflow.Enabled = body.Enabled ?? workflow.Enabled;
        WorkflowVersion? version;
        if (body.Definition is not null)
        {
            var (spec, problem) = await CheckDefinitionAsync(body.Definition, activities, catalog, terms, caller.TenantId, cancellationToken);
            if (problem is not null)
            {
                return problem;
            }

            // A new version: running runs keep theirs.
            workflow.CurrentVersion++;
            workflow.TriggerTypes = TriggerTypes(spec!);
            version = new WorkflowVersion { Id = Ids.New(), TenantId = caller.TenantId, WorkflowId = workflow.Id, Number = workflow.CurrentVersion, Definition = WorkflowJson.Serialize(spec!), CreatedAt = time.GetUtcNow() };
            db.WorkflowVersions.Add(version);
        }
        else
        {
            version = await WorkflowVersions.FindAsync(db, caller.TenantId, workflow.Id, workflow.CurrentVersion, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(http.Response, workflow.Version);
        return TypedResults.Ok(ToDto(workflow, version!));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid workflowId, Caller caller, IWorkspaceAccess workspaces, WorkflowsDbContext db, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Manage, workspaces, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (await FindAsync(db, caller.TenantId, workspaceId, workflowId, cancellationToken) is not { } workflow)
        {
            return ApiErrors.NotFound();
        }

        // Versions and runs go with the workflow (foreign key cascade).
        db.Workflows.Remove(workflow);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Starts a manual workflow: on an item (202 with the run), on several selected items (<c>itemIds</c>: 200 with the runs)
    /// or without an item. When the manual trigger names a list, items of that list are required. <c>inputs</c> are checked
    /// against the trigger's <c>inputs</c> schema and become run variables.
    /// </summary>
    private static async Task<Results<Accepted<RunDto>, Ok<List<RunDto>>, ValidationProblem, ProblemHttpResult>> StartAsync(
        Guid workspaceId, Guid workflowId, StartRunRequest body, Caller caller, IWorkspaceAccess workspaces, WorkflowsDbContext db, WorkflowStarter starter,
        IListItemStore items, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Contribute, workspaces, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (await FindAsync(db, caller.TenantId, workspaceId, workflowId, cancellationToken) is not { } workflow
            || await WorkflowVersions.FindAsync(db, caller.TenantId, workflowId, workflow.CurrentVersion, cancellationToken) is not { } version)
        {
            return ApiErrors.NotFound();
        }

        var spec = WorkflowJson.Deserialize(version.Definition);
        var manual = spec.AllTriggers.FirstOrDefault(t => t.Type == WorkflowTriggers.Manual);
        if (!workflow.Enabled || manual is null)
        {
            return ApiErrors.Conflict("notManual", "The workflow is turned off or has no manual trigger.");
        }

        if (WorkflowInputs.Check(manual.Inputs, body.Inputs) is { } inputError)
        {
            return ApiErrors.Validation("inputs", inputError);
        }

        IReadOnlyList<Guid> selected = body.ItemIds ?? (body.ItemId is { } single ? [single] : []);
        if (selected.Count > WorkflowStarter.MaxItemsPerStart)
        {
            return ApiErrors.Validation("itemIds", $"At most {WorkflowStarter.MaxItemsPerStart} items can be selected.");
        }

        if (manual.List is { } listName && selected.Count == 0)
        {
            return ApiErrors.Validation("itemIds", $"The workflow runs on items of the list '{listName}': select items.");
        }

        // The caller must be able to read the items the runs work on, in the trigger's list.
        if (selected.Count > 0)
        {
            if (body.ListId is not { } listId || await items.GetListAsync(workspaceId, listId, cancellationToken) is not { } list
                || (manual.List is { } wanted && list.Name != wanted))
            {
                return ApiErrors.Validation("listId", "Give the listId of the items (the list the trigger names).");
            }

            foreach (var itemId in selected.Distinct())
            {
                if (await items.GetAsync(workspaceId, listId, itemId, cancellationToken) is null)
                {
                    return ApiErrors.Validation("itemIds", $"The item {itemId} was not found in the list.");
                }
            }
        }

        if (body.ItemIds is not null)
        {
            var runs = await starter.StartManualAsync(workflow, spec, body.ListId, [.. selected.Distinct().Select(id => (Guid?)id)], body.Inputs, caller.UserId, cancellationToken);
            return TypedResults.Ok(runs.Select(ToDto).ToList());
        }

        var run = await starter.StartManualAsync(workflow, spec, body.ItemId is null ? null : body.ListId, body.ItemId, body.Inputs, caller.UserId, cancellationToken);
        return TypedResults.Accepted($"/v1.0/workspaces/{workspaceId}/workflows/runs/{run.Id}", ToDto(run));
    }

    private static async Task<Results<Ok<Page<RunDto>>, ProblemHttpResult>> ListRunsAsync(
        Guid workspaceId, Guid workflowId, HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        Caller caller, IWorkspaceAccess workspaces, WorkflowsDbContext database, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Read, workspaces, cancellationToken) is { } problem)
        {
            return problem;
        }

        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var workspace = workspaceId;
        var workflow = workflowId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var runs = page.After is { } before
            ? await db.WorkflowRuns.Where(r => r.TenantId == tenant && r.WorkspaceId == workspace && r.WorkflowId == workflow && r.Id.CompareTo(before) < 0).OrderByDescending(r => r.Id).Take(take).ToListAsync(ct)
            : await db.WorkflowRuns.Where(r => r.TenantId == tenant && r.WorkspaceId == workspace && r.WorkflowId == workflow).OrderByDescending(r => r.Id).Take(take).ToListAsync(ct);
        return TypedResults.Ok(Page.Create([.. runs.Select(ToDto)], page, request, r => r.Id));
    }

    private static async Task<Results<Ok<RunDto>, ProblemHttpResult>> GetRunAsync(
        Guid workspaceId, Guid runId, Caller caller, IWorkspaceAccess workspaces, WorkflowsDbContext database, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Read, workspaces, cancellationToken) is { } problem)
        {
            return problem;
        }

        var db = database;
        var tenant = caller.TenantId;
        var workspace = workspaceId;
        var id = runId;
        var ct = cancellationToken;
        return await db.WorkflowRuns.Where(r => r.TenantId == tenant && r.WorkspaceId == workspace && r.Id == id).FirstOrDefaultAsync(ct) is { } run
            ? TypedResults.Ok(ToDto(run))
            : ApiErrors.NotFound();
    }

    private static Task<WorkflowRun?> FindRunAsync(WorkflowsDbContext database, Guid tenantId, Guid workspaceId, Guid runId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var workspace = workspaceId;
        var id = runId;
        var ct = cancellationToken;
        return db.WorkflowRuns.Where(r => r.TenantId == tenant && r.WorkspaceId == workspace && r.Id == id).FirstOrDefaultAsync(ct);
    }

    private static async Task<Results<Ok<RunDto>, ProblemHttpResult>> CancelRunAsync(
        Guid workspaceId, Guid runId, Caller caller, IWorkspaceAccess workspaces, WorkflowsDbContext db, RunService runs, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Manage, workspaces, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (await FindRunAsync(db, caller.TenantId, workspaceId, runId, cancellationToken) is not { } run)
        {
            return ApiErrors.NotFound();
        }

        if (run.Status is RunStatus.Running or RunStatus.Waiting)
        {
            await runs.CancelAsync(run, cancellationToken);
        }

        return TypedResults.Ok(ToDto(run));
    }

    private static async Task<Results<Ok<RunDto>, ProblemHttpResult>> RetryRunAsync(
        Guid workspaceId, Guid runId, Caller caller, IWorkspaceAccess workspaces, WorkflowsDbContext db, RunService runs, CancellationToken cancellationToken)
    {
        if (await CheckAsync(caller, workspaceId, WorkspaceAccessLevel.Manage, workspaces, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (await FindRunAsync(db, caller.TenantId, workspaceId, runId, cancellationToken) is not { } run)
        {
            return ApiErrors.NotFound();
        }

        return await runs.RetryAsync(run, cancellationToken)
            ? TypedResults.Ok(ToDto(run))
            : ApiErrors.Conflict("notRetryable", "Only failed runs that stopped at a node can be retried.");
    }

    /// <summary>A key for a new workflow made from its name, unique in the workspace (<c>name</c>, <c>name-2</c>, …).</summary>
    internal static async Task<string> UniqueKeyAsync(WorkflowsDbContext database, Guid tenantId, Guid workspaceId, string name, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var workspace = workspaceId;
        var ct = cancellationToken;
        var workflows = await context.Workflows.AsNoTracking().Where(w => w.TenantId == tenant && w.WorkspaceId == workspace).ToListAsync(ct);
        var taken = workflows.Select(w => w.EventKey).ToHashSet(StringComparer.Ordinal);
        var key = WorkflowKeys.FromName(name);
        var candidate = key;
        for (var i = 2; taken.Contains(candidate); i++)
        {
            candidate = $"{key}-{i}";
        }

        return candidate;
    }
}
