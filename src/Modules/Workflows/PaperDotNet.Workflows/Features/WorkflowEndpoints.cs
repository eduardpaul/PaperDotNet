using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>A workflow: <c>trigger</c>, or several <c>triggers</c> (any of them starts a run), and its definition. <c>key</c> names its events (<c>wf.{key}.completed</c>; default: made from the name when it is created).</summary>
public sealed record WorkflowRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(2000)] string? Description,
    WorkflowTrigger? Trigger,
    [property: StringLength(4000)] string? Condition,
    IReadOnlyList<WorkflowStep>? Steps,
    bool Enabled = true,
    FlowDefinition? Flow = null,
    JsonObject? Variables = null,
    [property: StringLength(20)] string? Concurrency = null,
    IReadOnlyList<WorkflowTrigger>? Triggers = null,
    [property: StringLength(WorkflowKeys.MaxLength)] string? Key = null, string? Scope = null, JsonObject? InputSchema = null);

/// <summary>A workflow with the definition of its current <c>version</c> (runs keep the version they started with): its <c>trigger</c> or <c>triggers</c> (as it was defined), <c>steps</c> or a <c>flow</c>, the initial <c>variables</c>, and <c>concurrency</c> (runs on the same item: <c>parallel</c>, <c>skip</c> or <c>replace</c>).</summary>
public sealed record WorkflowResponse(
    Guid Id, Guid WorkspaceId, string Name, string? Description, bool Enabled, int Version, WorkflowTrigger? Trigger, string? Condition,
    IReadOnlyList<WorkflowStep>? Steps, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, FlowDefinition? Flow = null, JsonObject? Variables = null,
    string? BuiltIn = null, string? CopiedFrom = null, string? Concurrency = null, IReadOnlyList<WorkflowTrigger>? Triggers = null, string? Key = null,
    Guid? ListId = null, string? Scope = null, JsonObject? InputSchema = null)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

/// <summary>
/// A built-in workflow (EVT-12) and its state in the workspace: its <c>parameters</c> (JSON Schema), whether the server
/// has what it needs (<c>available</c>), and when it was turned on, the <c>workflowId</c> it runs as and its <c>values</c>.
/// </summary>
public sealed record BuiltInWorkflowResponse(
    string Key, string Name, string Description, JsonObject? Parameters, string? Requires, bool Available, bool Enabled, Guid? WorkflowId, JsonObject? Values,
    BuiltInScope Scope = BuiltInScope.Workspace, bool EnabledByDefault = false, bool AllowManualLaunch = false, JsonObject? InputSchema = null)
{
    /// <summary>
    /// Once it was turned on in the workspace: the ETag for <c>If-Match</c> on changes (the workflow's; the same as the
    /// <c>ETag</c> header).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

/// <summary>Turns a built-in workflow on or off in the workspace; <c>parameters</c> (default: the ones it had) fill in its definition.</summary>
public sealed record BuiltInSettingsRequest(bool Enabled, JsonObject? Parameters = null);

/// <summary>Copies a built-in workflow into a workflow of the workspace named <c>name</c>, to change it.</summary>
public sealed record CopyBuiltInRequest([property: Required, StringLength(200, MinimumLength = 1)] string Name, JsonObject? Parameters = null);

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
    DateTimeOffset? CompletedAt, string? Node, string? FailedNode, JsonObject Outputs, JsonObject Variables, JsonObject? ExecutionContext = null);

public sealed record ApprovalResponse(
    Guid Id, Guid RunId, string StepName, string Title, Guid WorkspaceId, Guid? ListId, Guid? ItemId, ApprovalStatus Status,
    DateTimeOffset? DueAt, bool Escalated, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Comment, DateTimeOffset CreatedAt, JsonObject? InputSchema = null, JsonObject? Inputs = null)
{
    public ApprovalReviewReference? Review { get; init; }

    internal static ApprovalResponse From(ApprovalRequest a) =>
        new(a.Id, a.RunId, a.StepName, a.Title, a.WorkspaceId, a.ListId, a.ItemId, a.Status, a.DueAt, a.Escalated, a.DecidedBy, a.DecidedAt, a.Comment, a.CreatedAt, a.InputSchema is null ? null : JsonNode.Parse(a.InputSchema) as JsonObject,
            a.Inputs is null ? null : JsonNode.Parse(a.Inputs) as JsonObject)
        { Review = a.ReviewType is { } type ? new(type, a.ReviewKey ?? "") : null };
}

/// <summary><c>outcome</c>: <c>approved</c> or <c>rejected</c>.</summary>
public sealed record DecisionRequest([property: Required] string Outcome, [property: StringLength(2000)] string? Comment, JsonObject? Inputs = null);

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
        group.MapGet("/builtIns", ListBuiltInsAsync).RequireScope(WorkflowScopes.Read).WithName("ListBuiltInWorkflows");
        group.MapPut("/builtIns/{key}", SetBuiltInAsync).RequireScope(WorkflowScopes.Write).WithName("SetBuiltInWorkflow");
        group.MapPost("/builtIns/{key}/copy", CopyBuiltInAsync).RequireScope(WorkflowScopes.Write).WithName("CopyBuiltInWorkflow");
        group.MapGet("/runs", ListRunsAsync).RequireScope(WorkflowScopes.Read).WithName("ListWorkflowRuns").WithQueryEnum<RunStatus>("status");
        group.MapGet("/runs/{id:guid}", GetRunAsync).RequireScope(WorkflowScopes.Read).WithName("GetWorkflowRun");
        group.MapPost("/runs/{id:guid}/cancel", CancelRunAsync).RequireScope(WorkflowScopes.Write).WithName("CancelWorkflowRun");
        group.MapPost("/runs/{id:guid}/retry", RetryRunAsync).RequireScope(WorkflowScopes.Write).WithName("RetryWorkflowRun");
        group.MapPost("/{id:guid}/webhook", WebhookAsync).RequireScope(WorkflowScopes.Write).WithName("TriggerWorkflowWebhook");
        group.MapPost("/{id:guid}/runs", StartRunsAsync).RequireScope(WorkflowScopes.Write).WithName("StartWorkflowRuns");

        endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/workflows", "Workflows")
            .MapPost("", StartAsync).RequireScope(WorkflowScopes.Write).WithName("StartWorkflow");

        var library = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/workflows/builtIns", "Workflows");
        library.MapGet("", ListLibraryBuiltInsAsync).RequireScope(WorkflowScopes.Read).WithName("ListLibraryBuiltInWorkflows");
        library.MapPost("/{key}/runs", StartLibraryBuiltInRunsAsync).RequireScope(WorkflowScopes.Write).WithName("StartLibraryBuiltInWorkflowRuns");
        library.MapPut("/{key}", SetLibraryBuiltInAsync).RequireScope(WorkflowScopes.Write).WithName("SetLibraryBuiltInWorkflow");

        var me = endpoints.MapV1Group("me/approvals", "Workflows");
        me.MapGet("", ListApprovalsAsync).RequireScope(WorkflowScopes.Read).WithName("ListMyApprovals").WithQueryEnum<ApprovalStatus>("status");
        me.MapGet("/{id:guid}/review", GetReviewAsync).RequireScope(WorkflowScopes.Read).WithName("GetApprovalReview");
        me.MapGet("/{id:guid}/review/content/{part}", OpenReviewAsync).RequireScope(WorkflowScopes.Read).WithName("GetApprovalReviewContent").ProducesBinary();
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

        var workflows = await db.Workflows.AsNoTracking().Where(a => a.WorkspaceId == workspaceId)
            .Join(db.Versions, w => new { WorkflowId = w.Id, Number = w.CurrentVersion }, v => new { v.WorkflowId, v.Number },
                (workflow, version) => new { Workflow = workflow, Version = version })
            .OrderBy(row => row.Workflow.Name).ToListAsync(ct);
        return TypedResults.Ok(workflows.Select(row => ToResponse(row.Workflow, row.Version)).ToList());
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

        var key = request.Key?.Trim() is { Length: > 0 } chosen ? chosen : await WorkflowWriter.KeyForAsync(db, workspaceId, name, ct);
        if (await KeyProblemAsync(db, workspaceId, key, null, ct) is { } keyProblem)
        {
            return keyProblem;
        }

        var workflow = WorkflowWriter.Create(db, workspaceId, name, request.Description, request.Enabled, spec!, key);
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

        if (workflow.BuiltInKey is not null)
        {
            return ApiErrors.Conflict("builtInReadOnly", "A built-in workflow cannot be changed; set its parameters, or copy it to change it.");
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

        if (request.Key?.Trim() is { Length: > 0 } key && key != workflow.EventKey)
        {
            if (await KeyProblemAsync(db, workspaceId, key, workflow.Id, ct) is { } keyProblem)
            {
                return keyProblem;
            }

            workflow.Key = key;
        }

        workflow.Key ??= workflow.EventKey;
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

    /// <summary>Why a key chosen for a workflow cannot be used: not valid (400) or used by another workflow (409).</summary>
    private static async Task<ProblemHttpResult?> KeyProblemAsync(WorkflowsDbContext db, Guid workspaceId, string key, Guid? except, CancellationToken ct)
    {
        if (!WorkflowKeys.IsValid(key))
        {
            return ApiErrors.Problem(StatusCodes.Status400BadRequest, "invalidKey",
                "A key has lower case letters, digits, dashes and underscores, e.g. check-big-bills.");
        }

        return await WorkflowWriter.KeyTakenAsync(db, workspaceId, key, except, ct)
            ? ApiErrors.Conflict("keyAlreadyExists", $"A workflow with the key '{key}' already exists in the workspace.")
            : null;
    }

    private static async Task<(WorkflowSpec? Spec, ValidationProblem? Problem)> ValidateAsync(
        Guid workspaceId, WorkflowRequest request, WorkflowValidator validator, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return (null, invalid);
        }

        var spec = new WorkflowSpec(
            request.Trigger, string.IsNullOrWhiteSpace(request.Condition) ? null : request.Condition.Trim(),
            request.Flow is null ? request.Steps ?? [] : request.Steps is { Count: > 0 } ? request.Steps : null, request.Flow, request.Variables,
            string.IsNullOrWhiteSpace(request.Concurrency) ? null : request.Concurrency, request.Triggers, request.Scope, request.InputSchema);
        var errors = await validator.ValidateAsync(workspaceId, spec, ct);
        return errors.Count == 0 ? (spec, null) : (null, ApiErrors.Validation(new Dictionary<string, string[]> { ["workflow"] = [.. errors] }));
    }

    private static async Task<WorkflowResponse> ToResponseAsync(WorkflowsDbContext db, WorkflowDefinition workflow, CancellationToken ct)
    {
        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == workflow.Id && v.Number == workflow.CurrentVersion, ct);
        return ToResponse(workflow, version);
    }

    private static WorkflowResponse ToResponse(WorkflowDefinition workflow, WorkflowVersion version)
    {
        var spec = DefinitionJson.Deserialize<WorkflowSpec>(version.Definition);
        return new WorkflowResponse(workflow.Id, workflow.WorkspaceId, workflow.Name, workflow.Description, workflow.Enabled,
            workflow.CurrentVersion, spec.Trigger, spec.Condition, spec.Steps, workflow.CreatedAt, workflow.UpdatedAt, spec.Flow, spec.Variables,
            workflow.BuiltInKey, workflow.CopiedFrom, spec.Concurrency, spec.Triggers, workflow.EventKey, workflow.ListId, spec.Scope, spec.InputSchema)
        { ETag = ETags.From(workflow.Version) };
    }

    // ---- Built-in workflows ---------------------------------------------------------

    private static async Task<Results<Ok<List<BuiltInWorkflowResponse>>, ProblemHttpResult>> ListBuiltInsAsync(
        Guid workspaceId, IWorkspaceAccess workspaces, BuiltInWorkflows builtIns, WorkflowsDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var rows = await db.Workflows.AsNoTracking().Where(w => w.WorkspaceId == workspaceId && w.BuiltInKey != null && w.ListId == null).ToListAsync(ct);
        return TypedResults.Ok((await builtIns.ListAsync(ct))
            .Where(w => w.Scope == BuiltInScope.Workspace)
            .Select(w => ToResponse(builtIns, w, rows.FirstOrDefault(r => r.BuiltInKey == w.Key)))
            .ToList());
    }

    /// <summary>
    /// The built-in workflows of a document library (ADR-0038: e.g. reading the text, thumbnails, page images, OCR), on or off
    /// in it. Those on by default are created on first use.
    /// </summary>
    private static async Task<Results<Ok<List<BuiltInWorkflowResponse>>, ProblemHttpResult>> ListLibraryBuiltInsAsync(
        Guid workspaceId, Guid listId, IWorkspaceAccess workspaces, IListItemStore items, BuiltInWorkflows builtIns, WorkflowsDbContext db,
        ITenantContext tenant, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        if (await items.GetListAsync(workspaceId, listId, ct) is not { } list)
        {
            return ApiErrors.NotFound();
        }

        await builtIns.EnsureDefaultsAsync(list, tenant.TenantId!.Value, ct);
        var rows = await db.Workflows.AsNoTracking().Where(w => w.WorkspaceId == workspaceId && w.ListId == listId && w.BuiltInKey != null).ToListAsync(ct);
        return TypedResults.Ok((await builtIns.ListAsync(ct))
            .Where(w => w.Scope == BuiltInScope.List || (w.Scope == BuiltInScope.Library && list.IsLibrary))
            .Select(w => ToResponse(builtIns, w, rows.FirstOrDefault(r => r.BuiltInKey == w.Key), list.Name))
            .ToList());
    }

    /// <summary>Turns a built-in workflow on or off in a document library; once it has a row, changes need <c>If-Match</c> with its ETag.</summary>
    private static async Task<Results<Ok<BuiltInWorkflowResponse>, ValidationProblem, ProblemHttpResult>> SetLibraryBuiltInAsync(
        Guid workspaceId, Guid listId, string key, BuiltInSettingsRequest request, IWorkspaceAccess workspaces, IListItemStore items,
        BuiltInWorkflows builtIns, WorkflowsDbContext db, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        if (await items.GetListAsync(workspaceId, listId, ct) is not { } list || await builtIns.FindAsync(key, ct) is not { Scope: BuiltInScope.Library or BuiltInScope.List } workflow)
        {
            return ApiErrors.NotFound();
        }

        if (await builtIns.RowAsync(workspaceId, key, ct, listId) is { } existing && CheckIfMatch(db, existing, http) is { } precondition)
        {
            return precondition;
        }

        var (row, errors, nameTaken) = await builtIns.SetAsync(workspaceId, workflow, request.Enabled, request.Parameters, ct, list);
        if (nameTaken)
        {
            return ApiErrors.Conflict("nameAlreadyExists", errors[0]);
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["parameters"] = [.. errors] });
        }

        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        if (row is not null)
        {
            ETags.Set(response, row.Version);
        }

        return TypedResults.Ok(ToResponse(builtIns, workflow, row, list.Name));
    }

    /// <summary>
    /// Turns a built-in workflow on (checked like a saved workflow) or off in the workspace. Once it was turned on, changes
    /// need <c>If-Match</c> with its ETag.
    /// </summary>
    private static async Task<Results<Ok<BuiltInWorkflowResponse>, ValidationProblem, ProblemHttpResult>> SetBuiltInAsync(
        Guid workspaceId, string key, BuiltInSettingsRequest request, IWorkspaceAccess workspaces, BuiltInWorkflows builtIns, WorkflowsDbContext db,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        if (await builtIns.FindAsync(key, ct) is not { } workflow)
        {
            return ApiErrors.NotFound();
        }

        if (await builtIns.RowAsync(workspaceId, key, ct) is { } existing && CheckIfMatch(db, existing, http) is { } precondition)
        {
            return precondition;
        }

        var (row, errors, nameTaken) = await builtIns.SetAsync(workspaceId, workflow, request.Enabled, request.Parameters, ct);
        if (nameTaken)
        {
            return ApiErrors.Conflict("nameAlreadyExists", errors[0]);
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["parameters"] = [.. errors] });
        }

        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        if (row is not null)
        {
            ETags.Set(response, row.Version);
        }

        return TypedResults.Ok(ToResponse(builtIns, workflow, row));
    }

    private static async Task<Results<Created<WorkflowResponse>, ValidationProblem, ProblemHttpResult>> CopyBuiltInAsync(
        Guid workspaceId, string key, CopyBuiltInRequest request, IWorkspaceAccess workspaces, BuiltInWorkflows builtIns, WorkflowsDbContext db,
        HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        if (await builtIns.FindAsync(key, ct) is not { } workflow)
        {
            return ApiErrors.NotFound();
        }

        var (copy, errors, nameTaken) = await builtIns.CopyAsync(workspaceId, workflow, request.Name.Trim(), request.Parameters, ct);
        if (nameTaken)
        {
            return ApiErrors.Conflict("nameAlreadyExists", errors[0]);
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["parameters"] = [.. errors] });
        }

        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        ETags.Set(response, copy!.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/workflows/{copy.Id}", await ToResponseAsync(db, copy, ct));
    }

    private static BuiltInWorkflowResponse ToResponse(BuiltInWorkflows builtIns, BuiltInWorkflow workflow, WorkflowDefinition? row, string? listName = null)
    {
        var spec = workflow.AllowManualLaunch ? BuiltInWorkflows.Resolve(workflow, BuiltInWorkflows.Values(row), listName).Spec : null;
        var inputSchema = spec?.InputSchema ?? spec?.AllTriggers.FirstOrDefault(t => t.Type == WorkflowTriggers.Manual)?.Inputs;
        return new(workflow.Key, workflow.Name, workflow.Description, workflow.Parameters?.DeepClone().AsObject(), workflow.Requires, builtIns.IsAvailable(workflow),
            row?.Enabled == true, row?.Id, BuiltInWorkflows.Values(row), workflow.Scope, workflow.EnabledByDefault, workflow.AllowManualLaunch, inputSchema)
        { ETag = row is null ? null : ETags.From(row.Version) };
    }

    // ---- Runs ------------------------------------------------------------------------

    /// <summary>Manually runs an opted-in built-in on library items without turning automatic processing on.</summary>
    private static async Task<Results<Ok<List<RunResponse>>, ValidationProblem, ProblemHttpResult>> StartLibraryBuiltInRunsAsync(
        Guid workspaceId, Guid listId, string key, StartRunsRequest request, IWorkspaceAccess workspaces, IListItemStore items,
        BuiltInWorkflows builtIns, WorkflowStarter starter, WorkflowsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        if (await items.GetListAsync(workspaceId, listId, ct) is not { } list
            || await builtIns.FindAsync(key, ct) is not { Scope: BuiltInScope.Library or BuiltInScope.List, AllowManualLaunch: true } builtIn || !builtIns.IsAvailable(builtIn))
        {
            return ApiErrors.NotFound();
        }

        var ids = request.ItemIds?.Distinct().ToList() ?? [];
        if (ids.Count is 0 or > WorkflowStarter.MaxItemsPerStart || request.ListId is { } requestedList && requestedList != listId)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["itemIds"] = [$"Choose 1–{WorkflowStarter.MaxItemsPerStart} items from this library."] });
        }

        var targets = new List<WorkflowItem>();
        foreach (var itemId in ids)
        {
            if (await items.GetAsync(workspaceId, listId, itemId, ct) is not { } item)
            {
                return ApiErrors.NotFound();
            }
            if (item.Access < WorkspaceAccessLevel.Contribute)
            {
                return ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "Contributing to each item is required.");
            }
            targets.Add(new WorkflowItem(workspaceId, listId, itemId));
        }

        var (workflow, errors, nameTaken) = await builtIns.PrepareManualAsync(list, builtIn, ct);
        if (nameTaken)
        {
            return ApiErrors.Conflict("nameAlreadyExists", errors[0]);
        }
        if (workflow is null || errors.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["workflow"] = [.. errors] });
        }

        var (runs, error) = await starter.StartManualAsync(workflow, targets, request.Inputs, user.UserId, ct);
        return error is not null
            ? ApiErrors.Validation(new Dictionary<string, string[]> { ["workflow"] = [error] })
            : TypedResults.Ok(await ToResponsesAsync(db, runs, ct));
    }

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

        if (runs.Count == 0)
        {
            return ApiErrors.Conflict("runGoing", $"A run of '{workflow.Name}' on this item is still going (concurrency: skip).");
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

        var result = await ToResponsesAsync(db, runs, ct);

        return TypedResults.Ok(result);
    }

    /// <summary>Authenticated webhook launch; the body is validated against inputSchema.</summary>
    private static async Task<Results<Ok<List<RunResponse>>, ValidationProblem, ProblemHttpResult>> WebhookAsync(
        Guid workspaceId, Guid id, JsonObject inputs, IWorkspaceAccess workspaces, WorkflowStarter starter,
        WorkflowsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Contribute, ct) is { } denied)
        {
            return denied;
        }

        var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id && w.WorkspaceId == workspaceId, ct);
        if (workflow is null)
        {
            return ApiErrors.NotFound();
        }

        var (runs, error) = await starter.StartOnDemandAsync(workflow, [], inputs, user.UserId, WorkflowTriggers.Webhook, ct);
        if (error is not null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["inputs"] = [error] });
        }

        var result = await ToResponsesAsync(db, runs, ct);

        return TypedResults.Ok(result);
    }

    /// <summary>Runs in the workspace, newest first; filter by <c>workflowId</c>, <c>itemId</c> or <c>status</c>.</summary>
    private static async Task<Results<Ok<Page<RunResponse>>, ValidationProblem, ProblemHttpResult>> ListRunsAsync(
        Guid workspaceId, Guid? workflowId, Guid? itemId, string? status, IWorkspaceAccess workspaces, WorkflowsDbContext db, HttpRequest http, IItemAccess itemAccess,
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
        var readable = (await itemAccess.GetReadableItemIdsAsync(workspaceId, ct)).ToArray();
        var canManage = await workspaces.GetPermissionAsync(workspaceId, ct) >= WorkspaceAccessLevel.Manage;
        var query = db.Runs.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && (canManage || r.ItemId == null || EF.Parameter(readable).Contains(r.ItemId.Value)));
        query = itemId is { } item ? query.Where(r => r.ItemId == item) : query;
        query = workflowId is { } workflow ? query.Where(r => r.WorkflowId == workflow) : query;
        query = statusFilter is { } wanted ? query.Where(r => r.Status == wanted) : query;
        if (page.After is { } after)
        {
            query = query.Where(r => r.Id.CompareTo(after) < 0);
        }

        var runs = await query.OrderByDescending(r => r.Id).Take(page.Top + 1).ToListAsync(ct);
        var result = await ToResponsesAsync(db, runs, ct);

        return TypedResults.Ok(Page.Create(result, page, http, r => r.Id));
    }

    private static async Task<Results<Ok<RunResponse>, ProblemHttpResult>> GetRunAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, WorkflowsDbContext db, IListItemStore items, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.WorkspaceId == workspaceId, ct);
        if (run?.ItemId is { } target && await workspaces.GetPermissionAsync(workspaceId, ct) < WorkspaceAccessLevel.Manage
            && await items.GetByIdAsync(target, ct) is null) { return ApiErrors.NotFound(); }
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
        return ToResponse(run, await db.Workflows.AsNoTracking().Where(a => a.Id == run.WorkflowId).Select(a => a.Name).FirstOrDefaultAsync(ct));
    }

    private static async Task<List<RunResponse>> ToResponsesAsync(WorkflowsDbContext db, List<WorkflowRun> runs, CancellationToken ct)
    {
        if (runs.Count == 0) return [];
        var ids = runs.Select(r => r.WorkflowId).Distinct().ToList();
        var names = await db.Workflows.AsNoTracking().Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        return runs.Select(run => ToResponse(run, names.GetValueOrDefault(run.WorkflowId))).ToList();
    }

    private static RunResponse ToResponse(WorkflowRun run, string? name)
    {
        var outputs = JsonNode.Parse(run.Outputs) as JsonObject ?? [];
        var outcomes = outputs.Where(o => o.Value?["outcome"] is JsonValue value && value.TryGetValue<string>(out _))
            .ToDictionary(o => o.Key, o => o.Value!["outcome"]!.GetValue<string>(), StringComparer.Ordinal);
        return new RunResponse(run.Id, run.WorkflowId, name, run.WorkflowVersion, run.WorkspaceId, run.ListId, run.ItemId, run.EventId, run.Status,
            outcomes, JsonNode.Parse(run.Log) as JsonArray ?? [], run.Error, run.StartedBy, run.StartedAt, run.CompletedAt, run.Node, run.FailedNode,
            outputs, JsonNode.Parse(run.Variables) as JsonObject ?? [],
            (run.Data is null ? null : JsonNode.Parse(run.Data)?["$workflowContext"]) as JsonObject);
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

        var (result, error) = await approvals.DecideAsync(id, user.UserId!.Value, request.Outcome, request.Comment, ct, request.Inputs);
        switch (result)
        {
            case RunService.DecisionResult.InvalidInputs:
                return ApiErrors.Validation(new Dictionary<string, string[]> { ["inputs"] = [error!] });
            case RunService.DecisionResult.NotFound:
                return ApiErrors.NotFound();
            case RunService.DecisionResult.InvalidReview:
                return ApiErrors.Conflict("reviewUnavailable", "The review is unavailable or its source file changed. Open the latest review.");
            case RunService.DecisionResult.AlreadyDecided:
                return ApiErrors.Conflict("alreadyDecided", "The approval was already decided or cancelled.");
        }

        return TypedResults.Ok(ApprovalResponse.From(await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == id, ct)));
    }

    private static async Task<(IApprovalReviewProvider Provider, ApprovalReviewContext Context)?> ReviewAsync(
        Guid id, WorkflowsDbContext db, ICurrentUser user, IListItemStore items, IEnumerable<IApprovalReviewProvider> providers, CancellationToken ct)
    {
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.Assignees.Contains(user.UserId!.Value), ct);
        if (approval?.ReviewType is not { } type || approval.ListId is not { } listId || approval.ItemId is not { } itemId
            || await items.GetAsync(approval.WorkspaceId, listId, itemId, ct) is null)
        {
            return null;
        }

        var provider = providers.SingleOrDefault(p => p.Type == type);
        return provider is not null && await provider.IsAvailableAsync(ct)
            ? (provider, new ApprovalReviewContext(id, approval.RunId, new WorkflowItem(approval.WorkspaceId, listId, itemId), approval.ReviewKey ?? "")) : null;
    }

    private static async Task<Results<Ok<ApprovalReviewData>, ProblemHttpResult>> GetReviewAsync(
        Guid id, WorkflowsDbContext db, ICurrentUser user, IListItemStore items, IEnumerable<IApprovalReviewProvider> providers, CancellationToken ct)
    {
        var review = await ReviewAsync(id, db, user, items, providers, ct);
        return review is { } found && await found.Provider.GetAsync(found.Context, ct) is { } data
            ? TypedResults.Ok(data) : ApiErrors.NotFound();
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> OpenReviewAsync(
        Guid id, string part, WorkflowsDbContext db, ICurrentUser user, IListItemStore items, IEnumerable<IApprovalReviewProvider> providers, CancellationToken ct)
    {
        var review = await ReviewAsync(id, db, user, items, providers, ct);
        return review is { } found && await found.Provider.OpenAsync(found.Context, part, ct) is { } content
            ? TypedResults.File(content.Content, content.MediaType, enableRangeProcessing: true) : ApiErrors.NotFound();
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
    public static WorkflowDefinition Create(
        WorkflowsDbContext db, Guid workspaceId, string name, string? description, bool enabled, WorkflowSpec spec, string key, Guid? listId = null)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Ids.New(),
            WorkspaceId = workspaceId,
            Name = name,
            Key = key,
            ListId = listId,
            Description = description,
            Enabled = enabled,
            Trigger = spec.TriggerTypes,
            CurrentVersion = 1,
        };
        db.Workflows.Add(workflow);
        db.Versions.Add(new WorkflowVersion { Id = Ids.New(), WorkflowId = workflow.Id, Number = 1, Definition = DefinitionJson.Serialize(spec) });
        return workflow;
    }

    /// <summary>A key made from <paramref name="name"/> that no workflow of the workspace has (a number is added when needed).</summary>
    public static async Task<string> KeyForAsync(WorkflowsDbContext db, Guid workspaceId, string name, CancellationToken ct)
    {
        var key = WorkflowKeys.FromName(name);
        for (var n = 2; await KeyTakenAsync(db, workspaceId, key, null, ct); n++)
        {
            key = $"{WorkflowKeys.FromName(name)[..Math.Min(WorkflowKeys.FromName(name).Length, WorkflowKeys.MaxLength - 4)]}-{n}";
        }

        return key;
    }

    /// <summary>Whether another workflow of the workspace (not <paramref name="except"/>) has the key (also one it gets from its name).</summary>
    public static async Task<bool> KeyTakenAsync(WorkflowsDbContext db, Guid workspaceId, string key, Guid? except, CancellationToken ct) =>
        (await db.Workflows.AsNoTracking().Where(w => w.WorkspaceId == workspaceId && w.Id != except)
            .Select(w => new { w.Key, w.BuiltInKey, w.Name }).ToListAsync(ct))
        .Any(w => (w.Key ?? w.BuiltInKey ?? WorkflowKeys.FromName(w.Name)) == key);

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
        workflow.Trigger = spec.TriggerTypes;
        db.Versions.Add(new WorkflowVersion { Id = Ids.New(), WorkflowId = workflow.Id, Number = workflow.CurrentVersion, Definition = json });
        return true;
    }
}

/// <summary>Validation of workflows against the catalog and the workspace's lists.</summary>
internal sealed class WorkflowValidator(TriggerCatalog triggers, ActionCatalog actions, IListItemStore items, ITermStore terms, IUserDirectory users)
{
    public async Task<List<string>> ValidateAsync(Guid workspaceId, WorkflowSpec spec, CancellationToken ct)
    {
        var errors = Definitions.Validate(spec, triggers.Keys, actions);
        if (errors.Count > 0)
        {
            return errors;
        }

        var schemas = new List<JsonObject?> { spec.InputSchema };
        schemas.AddRange(spec.AllTriggers.Select(trigger => trigger.Inputs));
        schemas.AddRange(Definitions.FlowOf(spec).Nodes.Values.Where(node => node.Activity == "approval").Select(node => node.Inputs?["inputSchema"] as JsonObject));
        foreach (var schema in schemas)
        {
            if (await DomainInputs.ValidateReferencesAsync(schema, items, terms, users, ct) is { } error)
            {
                errors.Add($"inputSchema: {error}");
            }
        }

        var several = spec.Triggers is not null;
        var lists = await items.AsSystem().GetListsAsync(workspaceId, null, ct);
        var checkedConditions = new HashSet<Guid>();
        foreach (var (trigger, index) in spec.AllTriggers.Select((t, i) => (t, i)))
        {
            var prefix = several ? $"triggers[{index}]: " : string.Empty;
            foreach (var path in trigger.Terms ?? [])
            {
                if (await terms.FindTermByPathAsync(path, ct) is null)
                {
                    errors.Add($"{prefix}The term '{path}' does not exist (use a path such as Group/Set/Term).");
                }
            }

            foreach (var leaf in TriggerConditions.Leaves(trigger.Parameters?.When))
            {
                if (leaf["term"] is { } termPath && await terms.FindTermByPathAsync(termPath.ToString(), ct) is null)
                {
                    errors.Add($"{prefix}The term '{termPath}' does not exist.");
                }
            }

            if (trigger.List is not { } listName)
            {
                continue;
            }

            var list = lists.FirstOrDefault(l => l.Name == listName);
            if (list is null)
            {
                errors.Add($"{prefix}The list '{listName}' does not exist in the workspace.");
                continue;
            }

            if (trigger.ContentType is { } type && !list.ContentTypes.Any(c => c.Name == type || c.Key == type))
            {
                errors.Add($"{prefix}The list '{listName}' has no content type '{type}'.");
            }

            if (trigger.Parameters is { } parameters)
            {
                var description = await items.AsSystem().DescribeListAsync(workspaceId, list.Id, ct);
                var contentTypes = description?.ContentTypes.Where(c => trigger.ContentType is null || c.Name == trigger.ContentType || c.Key == trigger.ContentType).ToList() ?? [];
                foreach (var leaf in TriggerConditions.Leaves(parameters.When).Where(node => node["target"]?.ToString() == "field"))
                {
                    var name = leaf["field"]!.ToString();
                    var fields = name == "title" ? [new ItemSnapshotField("text", false)]
                        : contentTypes.SelectMany(c => c.Fields).Where(f => f.Name == name).Select(f => new ItemSnapshotField(f.Type, f.AllowMultiple || f.Type == "keywords")).Distinct().ToList();
                    if (fields.Count == 0) errors.Add($"{prefix}The list '{list.Name}' has no field '{name}'.");
                    foreach (var field in fields)
                    {
                        if (TriggerConditions.ValidateField(leaf, field) is { } error) errors.Add(prefix + error);
                    }
                }
            }

            if (trigger.Type == WorkflowTriggers.Date)
            {
                var description = await items.AsSystem().DescribeListAsync(workspaceId, list.Id, ct);
                var field = description?.ContentTypes.SelectMany(c => c.Fields).FirstOrDefault(f => f.Name == trigger.Field);
                if (field?.Type is not ("date" or "dateTime"))
                {
                    errors.Add($"{prefix}The list '{listName}' has no date field '{trigger.Field}'.");
                }
            }

            // The condition is checked against every list a trigger names.
            if (spec.Condition is { } condition && checkedConditions.Add(list.Id))
            {
                var (_, error) = await items.AsSystem().QueryAsync(workspaceId, list.Id, new ListItemQuery($"id eq {Guid.Empty} and ({condition})", Top: 1), ct);
                if (error is not null)
                {
                    errors.Add($"condition: {error}");
                }
            }
        }

        return errors;
    }
}
