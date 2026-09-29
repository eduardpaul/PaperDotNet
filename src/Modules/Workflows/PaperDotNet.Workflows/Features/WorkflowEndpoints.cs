using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Workflows.Features;

public sealed record WorkflowRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(2000)] string? Description,
    WorkflowTrigger? Trigger,
    [property: StringLength(4000)] string? Condition,
    IReadOnlyList<WorkflowStep>? Steps,
    bool Enabled = true,
    FlowDefinition? Flow = null,
    JsonObject? Variables = null);

/// <summary>
/// A workflow with the definition of its current <c>version</c> (runs keep the version they started with): <c>steps</c>
/// or a <c>flow</c>, and the initial <c>variables</c>.
/// </summary>
public sealed record WorkflowResponse(
    Guid Id, Guid WorkspaceId, string Name, string? Description, bool Enabled, int Version, WorkflowTrigger Trigger, string? Condition,
    IReadOnlyList<WorkflowStep>? Steps, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, FlowDefinition? Flow = null, JsonObject? Variables = null)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

/// <summary>Starts a <c>manual</c> workflow on an item, by name; <c>inputs</c> become run variables (checked against the trigger's <c>inputs</c>).</summary>
public sealed record StartWorkflowRequest([property: Required] string Workflow, JsonObject? Inputs = null);

/// <summary>
/// Starts a <c>manual</c> workflow: once per item of <c>itemIds</c> (in <c>listId</c>, at most 100), or once without an
/// item when there are none (for workflows whose trigger has no list). <c>inputs</c> become run variables.
/// </summary>
public sealed record StartRunsRequest(Guid? ListId = null, IReadOnlyList<Guid>? ItemIds = null, JsonObject? Inputs = null);

/// <summary>
/// A run: its <c>node</c> (next or waited on), the approval <c>outcomes</c> by node, the <c>outputs</c> of the nodes that
/// ran, its <c>variables</c>, a log, and for failed runs the error and the <c>failedNode</c> it can be retried from.
/// </summary>
public sealed record RunResponse(
    Guid Id, Guid WorkflowId, string? Workflow, int WorkflowVersion, Guid WorkspaceId, Guid? ListId, Guid? ItemId, Guid? EventId,
    RunStatus Status, IReadOnlyDictionary<string, string> Outcomes, JsonArray Log, string? Error, Guid? StartedBy, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, string? Node, string? FailedNode, JsonObject Outputs, JsonObject Variables);

public sealed record ApprovalResponse(
    Guid Id, Guid RunId, string StepName, string Title, Guid WorkspaceId, Guid ListId, Guid ItemId, ApprovalStatus Status,
    DateTimeOffset? DueAt, bool Escalated, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Comment, DateTimeOffset CreatedAt)
{
    internal static ApprovalResponse From(ApprovalRequest a) =>
        new(a.Id, a.RunId, a.StepName, a.Title, a.WorkspaceId, a.ListId, a.ItemId, a.Status, a.DueAt, a.Escalated, a.DecidedBy, a.DecidedAt, a.Comment, a.CreatedAt);
}

/// <summary><c>outcome</c>: <c>approved</c> or <c>rejected</c>.</summary>
public sealed record DecisionRequest([property: Required] string Outcome, [property: StringLength(2000)] string? Comment);

public sealed record CatalogEntry(string Key, string Description);

/// <summary>
/// Workflow API (EVT-07…09, DOC-14): workflows of a workspace, their runs, approvals and the catalog of
/// triggers and actions. Reading needs Read access to the workspace; changes need Manage.
/// </summary>
internal static class WorkflowEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("workspaces/{workspaceId:guid}/workflows", "Workflows");
        group.MapGet("", ListAsync).RequireScope(WorkflowScopes.Read).WithName("ListWorkflows");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(WorkflowScopes.Read).WithName("GetWorkflow");
        group.MapPost("", CreateAsync).RequireScope(WorkflowScopes.Write).WithName("CreateWorkflow");
        group.MapPut("/{id:guid}", ReplaceAsync).RequireScope(WorkflowScopes.Write).WithName("ReplaceWorkflow");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireScope(WorkflowScopes.Write).WithName("DeleteWorkflow");
        group.MapGet("/runs", ListRunsAsync).RequireScope(WorkflowScopes.Read).WithName("ListWorkflowRuns").WithQueryEnum<RunStatus>("status");
        group.MapGet("/runs/{id:guid}", GetRunAsync).RequireScope(WorkflowScopes.Read).WithName("GetWorkflowRun");
        group.MapPost("/runs/{id:guid}/cancel", CancelRunAsync).RequireScope(WorkflowScopes.Write).WithName("CancelWorkflowRun");
        group.MapPost("/runs/{id:guid}/retry", RetryRunAsync).RequireScope(WorkflowScopes.Write).WithName("RetryWorkflowRun");
        group.MapPost("/{id:guid}/runs", StartRunsAsync).RequireScope(WorkflowScopes.Write).WithName("StartWorkflowRuns");

        endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/workflows", "Workflows")
            .MapPost("", StartAsync).RequireScope(WorkflowScopes.Write).WithName("StartWorkflow");

        var me = endpoints.MapV1Group("me/approvals", "Workflows");
        me.MapGet("", ListApprovalsAsync).RequireScope(WorkflowScopes.Read).WithName("ListMyApprovals").WithQueryEnum<ApprovalStatus>("status");
        me.MapPost("/{id:guid}/decision", DecideAsync).RequireScope(WorkflowScopes.Write).WithName("DecideApproval");

        var catalog = endpoints.MapV1Group("workflows", "Workflows");
        catalog.MapGet("/triggers", (TriggerCatalog triggers) => TypedResults.Ok(triggers.All.Select(t => new CatalogEntry(t.Key, t.Description)).ToList()))
            .RequireScope(WorkflowScopes.Read).WithName("ListWorkflowTriggers");
        catalog.MapGet("/activities", (ActionCatalog actions) => TypedResults.Ok(actions.Describe().ToList()))
            .RequireScope(WorkflowScopes.Read).WithName("ListWorkflowActivities");
    }

    // ---- Workflows ----------------------------------------------------------------

    private static async Task<Results<Ok<List<WorkflowResponse>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, IWorkspaceAccess workspaces, WorkflowsDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var workflows = await db.Workflows.AsNoTracking().Where(a => a.WorkspaceId == workspaceId).OrderBy(a => a.Name).ToListAsync(ct);
        var result = new List<WorkflowResponse>();
        foreach (var workflow in workflows)
        {
            result.Add(await ToResponseAsync(db, workflow, ct));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<WorkflowResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, WorkflowsDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == workspaceId, ct);
        if (workflow is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, workflow.Version);
        return TypedResults.Ok(await ToResponseAsync(db, workflow, ct));
    }

    private static async Task<Results<Created<WorkflowResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, WorkflowRequest request, IWorkspaceAccess workspaces, WorkflowsDbContext db, WorkflowValidator validator,
        HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var (spec, invalid) = await ValidateAsync(workspaceId, request, validator, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (await db.Workflows.AnyAsync(a => a.WorkspaceId == workspaceId && a.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A workflow named '{name}' already exists in the workspace.");
        }

        var workflow = WorkflowWriter.Create(db, workspaceId, name, request.Description, request.Enabled, spec!);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, workflow.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/workflows/{workflow.Id}", await ToResponseAsync(db, workflow, ct));
    }

    /// <summary>Replaces the workflow; a changed definition becomes a new version (running runs keep theirs).</summary>
    private static async Task<Results<Ok<WorkflowResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid workspaceId, Guid id, WorkflowRequest request, IWorkspaceAccess workspaces, WorkflowsDbContext db, WorkflowValidator validator,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var workflow = await db.Workflows.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == workspaceId, ct);
        if (workflow is null)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, workflow, http) is { } precondition)
        {
            return precondition;
        }

        var (spec, invalid) = await ValidateAsync(workspaceId, request, validator, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (name != workflow.Name && await db.Workflows.AnyAsync(a => a.WorkspaceId == workspaceId && a.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A workflow named '{name}' already exists in the workspace.");
        }

        workflow.Name = name;
        workflow.Description = request.Description;
        workflow.Enabled = request.Enabled;
        await WorkflowWriter.SetSpecAsync(db, workflow, spec!, ct);
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        ETags.Set(response, workflow.Version);
        return TypedResults.Ok(await ToResponseAsync(db, workflow, ct));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, WorkflowsDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var workflow = await db.Workflows.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == workspaceId, ct);
        if (workflow is null)
        {
            return ApiErrors.NotFound();
        }

        if (await db.Runs.AnyAsync(r => r.WorkflowId == id && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting), ct))
        {
            return ApiErrors.Conflict("workflowRunning", "The workflow has active runs; cancel them or disable the workflow.");
        }

        var runs = db.Runs.Where(r => r.WorkflowId == id).Select(r => r.Id);
        await db.Approvals.Where(a => runs.Contains(a.RunId)).ExecuteDeleteAsync(ct);
        await db.Bookmarks.Where(b => runs.Contains(b.RunId)).ExecuteDeleteAsync(ct);
        await db.Runs.Where(r => r.WorkflowId == id).ExecuteDeleteAsync(ct);
        await db.Versions.Where(v => v.WorkflowId == id).ExecuteDeleteAsync(ct);
        db.Workflows.Remove(workflow);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<(WorkflowSpec? Spec, ValidationProblem? Problem)> ValidateAsync(
        Guid workspaceId, WorkflowRequest request, WorkflowValidator validator, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return (null, invalid);
        }

        var spec = new WorkflowSpec(
            request.Trigger!, string.IsNullOrWhiteSpace(request.Condition) ? null : request.Condition.Trim(),
            request.Flow is null ? request.Steps ?? [] : request.Steps is { Count: > 0 } ? request.Steps : null, request.Flow, request.Variables);
        var errors = await validator.ValidateAsync(workspaceId, spec, ct);
        return errors.Count == 0 ? (spec, null) : (null, ApiErrors.Validation(new Dictionary<string, string[]> { ["workflow"] = [.. errors] }));
    }

    private static async Task<WorkflowResponse> ToResponseAsync(WorkflowsDbContext db, WorkflowDefinition workflow, CancellationToken ct)
    {
        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == workflow.Id && v.Number == workflow.CurrentVersion, ct);
        var spec = DefinitionJson.Deserialize<WorkflowSpec>(version.Definition);
        return new WorkflowResponse(workflow.Id, workflow.WorkspaceId, workflow.Name, workflow.Description, workflow.Enabled,
            workflow.CurrentVersion, spec.Trigger, spec.Condition, spec.Steps, workflow.CreatedAt, workflow.UpdatedAt, spec.Flow, spec.Variables)
        { ETag = ETags.From(workflow.Version) };
    }

    // ---- Runs ------------------------------------------------------------------------

    /// <summary>Starts a workflow with the <c>manual</c> trigger on the item (Contribute on the item is required).</summary>
    private static async Task<Results<Created<RunResponse>, ValidationProblem, ProblemHttpResult>> StartAsync(
        Guid workspaceId, Guid listId, Guid itemId, StartWorkflowRequest request, IListItemStore items, WorkflowStarter starter,
        WorkflowsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        if (item.Access < WorkspaceAccessLevel.Contribute)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "Contributing to the item is required.");
        }

        var name = request.Workflow.Trim();
        var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.Name == name, ct);
        if (workflow is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["workflow"] = [$"The workflow '{name}' does not exist in the workspace."] });
        }

        var (runs, error) = await starter.StartManualAsync(workflow, [new WorkflowItem(workspaceId, listId, itemId)], request.Inputs, user.UserId, ct);
        if (error is not null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["workflow"] = [error] });
        }

        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/workflows/runs/{runs[0].Id}", await ToResponseAsync(db, runs[0], ct));
    }

    /// <summary>
    /// Starts a <c>manual</c> workflow on several items (Contribute on each), or once without an item (Contribute on the
    /// workspace); all items are checked before anything starts.
    /// </summary>
    private static async Task<Results<Ok<List<RunResponse>>, ValidationProblem, ProblemHttpResult>> StartRunsAsync(
        Guid workspaceId, Guid id, StartRunsRequest request, IWorkspaceAccess workspaces, IListItemStore items, WorkflowStarter starter,
        WorkflowsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Contribute, ct) is { } denied)
        {
            return denied;
        }

        var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == workspaceId, ct);
        if (workflow is null)
        {
            return ApiErrors.NotFound();
        }

        var ids = request.ItemIds?.Distinct().ToList() ?? [];
        if (ids.Count > WorkflowStarter.MaxItemsPerStart || (ids.Count > 0 && request.ListId is null))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["itemIds"] = [$"Give listId and at most {WorkflowStarter.MaxItemsPerStart} items."] });
        }

        var targets = new List<WorkflowItem>();
        foreach (var itemId in ids)
        {
            var item = await items.GetAsync(workspaceId, request.ListId!.Value, itemId, ct);
            if (item is null)
            {
                return ApiErrors.Validation(new Dictionary<string, string[]> { ["itemIds"] = [$"The item {itemId} does not exist in the list."] });
            }

            if (item.Access < WorkspaceAccessLevel.Contribute)
            {
                return ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", $"Contributing to the item {itemId} is required.");
            }

            targets.Add(new WorkflowItem(workspaceId, request.ListId.Value, itemId));
        }

        var (runs, error) = await starter.StartManualAsync(workflow, targets, request.Inputs, user.UserId, ct);
        if (error is not null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["workflow"] = [error] });
        }

        var result = new List<RunResponse>();
        foreach (var run in runs)
        {
            result.Add(await ToResponseAsync(db, run, ct));
        }

        return TypedResults.Ok(result);
    }

    /// <summary>Runs in the workspace, newest first; filter by <c>workflowId</c>, <c>itemId</c> or <c>status</c>.</summary>
    private static async Task<Results<Ok<Page<RunResponse>>, ValidationProblem, ProblemHttpResult>> ListRunsAsync(
        Guid workspaceId, Guid? workflowId, Guid? itemId, string? status, IWorkspaceAccess workspaces, WorkflowsDbContext db, HttpRequest http,
        CancellationToken ct)
    {
        if (!EnumQuery.TryParse<RunStatus>(status, out var statusFilter))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["status"] = EnumQuery.Invalid<RunStatus>() });
        }

        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var page = PageRequest.From(http);
        var query = db.Runs.AsNoTracking().Where(r => r.WorkspaceId == workspaceId);
        query = itemId is { } item ? query.Where(r => r.ItemId == item) : query;
        query = workflowId is { } workflow ? query.Where(r => r.WorkflowId == workflow) : query;
        query = statusFilter is { } wanted ? query.Where(r => r.Status == wanted) : query;
        if (page.After is { } after)
        {
            query = query.Where(r => r.Id.CompareTo(after) < 0);
        }

        var runs = await query.OrderByDescending(r => r.Id).Take(page.Top + 1).ToListAsync(ct);
        var result = new List<RunResponse>();
        foreach (var run in runs)
        {
            result.Add(await ToResponseAsync(db, run, ct));
        }

        return TypedResults.Ok(Page.Create(result, page, http, r => r.Id));
    }

    private static async Task<Results<Ok<RunResponse>, ProblemHttpResult>> GetRunAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, WorkflowsDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.WorkspaceId == workspaceId, ct);
        return run is null ? ApiErrors.NotFound() : TypedResults.Ok(await ToResponseAsync(db, run, ct));
    }

    private static async Task<Results<Ok<RunResponse>, ProblemHttpResult>> CancelRunAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, WorkflowsDbContext db, RunService approvals, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == id && r.WorkspaceId == workspaceId, ct);
        if (run is null)
        {
            return ApiErrors.NotFound();
        }

        if (run.Status is RunStatus.Running or RunStatus.Waiting)
        {
            await approvals.CancelAsync(run, ct);
        }

        return TypedResults.Ok(await ToResponseAsync(db, await db.Runs.AsNoTracking().FirstAsync(r => r.Id == id, ct), ct));
    }

    /// <summary>Runs a failed run again from the node where it failed (<c>failedNode</c>); 409 when it cannot be retried.</summary>
    private static async Task<Results<Ok<RunResponse>, ProblemHttpResult>> RetryRunAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, WorkflowsDbContext db, RunService runs, ICurrentUser user, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == id && r.WorkspaceId == workspaceId, ct);
        if (run is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await runs.RetryAsync(run, user.UserId, ct))
        {
            return ApiErrors.Conflict("notRetryable", "Only runs that failed at a node can be retried.");
        }

        return TypedResults.Ok(await ToResponseAsync(db, await db.Runs.AsNoTracking().FirstAsync(r => r.Id == id, ct), ct));
    }

    private static async Task<RunResponse> ToResponseAsync(WorkflowsDbContext db, WorkflowRun run, CancellationToken ct)
    {
        var name = await db.Workflows.AsNoTracking().Where(a => a.Id == run.WorkflowId).Select(a => a.Name).FirstOrDefaultAsync(ct);
        var outputs = JsonNode.Parse(run.Outputs) as JsonObject ?? [];
        var outcomes = outputs.Where(o => o.Value?["outcome"] is JsonValue value && value.TryGetValue<string>(out _))
            .ToDictionary(o => o.Key, o => o.Value!["outcome"]!.GetValue<string>(), StringComparer.Ordinal);
        return new RunResponse(run.Id, run.WorkflowId, name, run.WorkflowVersion, run.WorkspaceId, run.ListId, run.ItemId, run.EventId, run.Status,
            outcomes, JsonNode.Parse(run.Log) as JsonArray ?? [], run.Error, run.StartedBy, run.StartedAt, run.CompletedAt, run.Node, run.FailedNode,
            outputs, JsonNode.Parse(run.Variables) as JsonObject ?? []);
    }

    // ---- Approvals ---------------------------------------------------------------------

    /// <summary>Approvals assigned to the caller, newest first; <c>status</c> filters (default: pending).</summary>
    private static async Task<Results<Ok<Page<ApprovalResponse>>, ValidationProblem>> ListApprovalsAsync(
        string? status, WorkflowsDbContext db, ICurrentUser user, HttpRequest http, CancellationToken ct)
    {
        if (!EnumQuery.TryParse<ApprovalStatus>(status, out var statusFilter))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["status"] = EnumQuery.Invalid<ApprovalStatus>() });
        }

        var page = PageRequest.From(http);
        var wanted = statusFilter ?? ApprovalStatus.Pending;
        var userId = user.UserId!.Value;
        var query = db.Approvals.AsNoTracking().Where(a => a.Status == wanted && a.Assignees.Contains(userId));
        if (page.After is { } after)
        {
            query = query.Where(a => a.Id.CompareTo(after) < 0);
        }

        var approvals = await query.OrderByDescending(a => a.Id).Take(page.Top + 1).ToListAsync(ct);
        return TypedResults.Ok(Page.Create(approvals.Select(ApprovalResponse.From).ToList(), page, http, a => a.Id));
    }

    private static async Task<Results<Ok<ApprovalResponse>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        Guid id, DecisionRequest request, RunService approvals, WorkflowsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        if (request.Outcome is not (ApprovalOutcomes.Approved or ApprovalOutcomes.Rejected))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["outcome"] = ["Use approved or rejected."] });
        }

        switch (await approvals.DecideAsync(id, user.UserId!.Value, request.Outcome, request.Comment, ct))
        {
            case RunService.DecisionResult.NotFound:
                return ApiErrors.NotFound();
            case RunService.DecisionResult.AlreadyDecided:
                return ApiErrors.Conflict("alreadyDecided", "The approval was already decided or cancelled.");
        }

        return TypedResults.Ok(ApprovalResponse.From(await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == id, ct)));
    }

    // ---- Helpers -----------------------------------------------------------------------

    private static async Task<ProblemHttpResult?> AccessAsync(IWorkspaceAccess workspaces, Guid workspaceId, WorkspaceAccessLevel needed, CancellationToken ct)
    {
        var level = await workspaces.GetPermissionAsync(workspaceId, ct);
        return level == WorkspaceAccessLevel.None ? ApiErrors.NotFound()
            : level < needed ? ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied",
                needed == WorkspaceAccessLevel.Manage ? "Managing the workspace is required." : "Contributing to the workspace is required.")
            : null;
    }

    private static ProblemHttpResult? CheckIfMatch<T>(WorkflowsDbContext db, T entity, HttpRequest http)
        where T : class, IVersioned
    {
        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (version != entity.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        db.Entry(entity).Property(e => e.Version).OriginalValue = version;
        return null;
    }

    private static async Task<ProblemHttpResult?> SaveAsync(WorkflowsDbContext db, CancellationToken ct)
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
}

/// <summary>Creates workflows and their versions (shared by the API and templates).</summary>
internal static class WorkflowWriter
{
    public static WorkflowDefinition Create(WorkflowsDbContext db, Guid workspaceId, string name, string? description, bool enabled, WorkflowSpec spec)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Ids.New(),
            WorkspaceId = workspaceId,
            Name = name,
            Description = description,
            Enabled = enabled,
            Trigger = spec.Trigger.Type,
            CurrentVersion = 1,
        };
        db.Workflows.Add(workflow);
        db.Versions.Add(new WorkflowVersion { Id = Ids.New(), WorkflowId = workflow.Id, Number = 1, Definition = DefinitionJson.Serialize(spec) });
        return workflow;
    }

    /// <summary>Adds a version when the definition changed; returns whether it did.</summary>
    public static async Task<bool> SetSpecAsync(WorkflowsDbContext db, WorkflowDefinition workflow, WorkflowSpec spec, CancellationToken ct)
    {
        var json = DefinitionJson.Serialize(spec);
        var current = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == workflow.Id && v.Number == workflow.CurrentVersion, ct);
        if (current.Definition == json)
        {
            return false;
        }

        workflow.CurrentVersion++;
        workflow.Trigger = spec.Trigger.Type;
        db.Versions.Add(new WorkflowVersion { Id = Ids.New(), WorkflowId = workflow.Id, Number = workflow.CurrentVersion, Definition = json });
        return true;
    }
}

/// <summary>Validation of workflows against the catalog and the workspace's lists.</summary>
internal sealed class WorkflowValidator(TriggerCatalog triggers, ActionCatalog actions, IListItemStore items, ITermStore terms)
{
    public async Task<List<string>> ValidateAsync(Guid workspaceId, WorkflowSpec spec, CancellationToken ct)
    {
        var errors = Definitions.Validate(spec, triggers.Keys, actions);
        foreach (var path in errors.Count > 0 ? [] : spec.Trigger.Terms ?? [])
        {
            if (await terms.FindTermByPathAsync(path, ct) is null)
            {
                errors.Add($"The term '{path}' does not exist (use a path such as Group/Set/Term).");
            }
        }

        if (errors.Count > 0 || spec.Trigger.List is not { } listName)
        {
            return errors;
        }

        var list = (await items.AsSystem().GetListsAsync(workspaceId, null, ct)).FirstOrDefault(l => l.Name == listName);
        if (list is null)
        {
            errors.Add($"The list '{listName}' does not exist in the workspace.");
            return errors;
        }

        if (spec.Trigger.ContentType is { } type && !list.ContentTypes.Any(c => c.Name == type || c.Key == type))
        {
            errors.Add($"The list '{listName}' has no content type '{type}'.");
        }

        if (spec.Trigger.Type == WorkflowTriggers.Date)
        {
            var description = await items.AsSystem().DescribeListAsync(workspaceId, list.Id, ct);
            var field = description?.ContentTypes.SelectMany(c => c.Fields).FirstOrDefault(f => f.Name == spec.Trigger.Field);
            if (field?.Type is not ("date" or "dateTime"))
            {
                errors.Add($"The list '{listName}' has no date field '{spec.Trigger.Field}'.");
            }
        }

        if (spec.Condition is { } condition)
        {
            var (_, error) = await items.AsSystem().QueryAsync(workspaceId, list.Id, new ListItemQuery($"id eq {Guid.Empty} and ({condition})", Top: 1), ct);
            if (error is not null)
            {
                errors.Add($"condition: {error}");
            }
        }

        return errors;
    }
}
