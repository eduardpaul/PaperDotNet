using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Automation.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Automation.Features;

public sealed record RuleRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    RuleTrigger? Trigger,
    [property: StringLength(4000)] string? Condition,
    IReadOnlyList<ActionDefinition>? Actions,
    bool Enabled = true);

public sealed record RuleResponse(
    Guid Id, Guid WorkspaceId, string Name, bool Enabled, RuleTrigger Trigger, string? Condition, IReadOnlyList<ActionDefinition> Actions,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record RuleRunResponse(Guid Id, Guid EventId, Guid? ItemId, RunStatus Status, string? Error, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);

public sealed record WorkflowRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(2000)] string? Description,
    IReadOnlyList<WorkflowStep>? Steps,
    bool Enabled = true);

/// <summary>A workflow with the steps of its current <c>version</c> (runs keep the version they started with).</summary>
public sealed record WorkflowResponse(
    Guid Id, Guid WorkspaceId, string Name, string? Description, bool Enabled, int Version, IReadOnlyList<WorkflowStep> Steps,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record StartWorkflowRequest([property: Required] string Workflow);

public sealed record RunResponse(
    Guid Id, Guid WorkflowId, string? Workflow, int WorkflowVersion, Guid WorkspaceId, Guid ListId, Guid ItemId, RunStatus Status,
    IReadOnlyDictionary<string, string> Outcomes, JsonArray Log, string? Error, Guid? StartedBy, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);

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
/// Automation API (EVT-07…09, DOC-14): rules and workflows of a workspace, runs, approvals and the catalog of
/// triggers and actions. Reading needs Read access to the workspace; changes need Manage.
/// </summary>
internal static class AutomationEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("workspaces/{workspaceId:guid}/automation", "Automation");
        group.MapGet("/rules", ListRulesAsync).RequireScope(AutomationScopes.Read).WithName("ListRules");
        group.MapGet("/rules/{id:guid}", GetRuleAsync).RequireScope(AutomationScopes.Read).WithName("GetRule");
        group.MapPost("/rules", CreateRuleAsync).RequireScope(AutomationScopes.Write).WithName("CreateRule");
        group.MapPut("/rules/{id:guid}", ReplaceRuleAsync).RequireScope(AutomationScopes.Write).WithName("ReplaceRule");
        group.MapDelete("/rules/{id:guid}", DeleteRuleAsync).RequireScope(AutomationScopes.Write).WithName("DeleteRule");
        group.MapGet("/rules/{id:guid}/runs", ListRuleRunsAsync).RequireScope(AutomationScopes.Read).WithName("ListRuleRuns");
        group.MapGet("/workflows", ListWorkflowsAsync).RequireScope(AutomationScopes.Read).WithName("ListWorkflows");
        group.MapGet("/workflows/{id:guid}", GetWorkflowAsync).RequireScope(AutomationScopes.Read).WithName("GetWorkflow");
        group.MapPost("/workflows", CreateWorkflowAsync).RequireScope(AutomationScopes.Write).WithName("CreateWorkflow");
        group.MapPut("/workflows/{id:guid}", ReplaceWorkflowAsync).RequireScope(AutomationScopes.Write).WithName("ReplaceWorkflow");
        group.MapDelete("/workflows/{id:guid}", DeleteWorkflowAsync).RequireScope(AutomationScopes.Write).WithName("DeleteWorkflow");
        group.MapGet("/runs", ListRunsAsync).RequireScope(AutomationScopes.Read).WithName("ListWorkflowRuns");
        group.MapGet("/runs/{id:guid}", GetRunAsync).RequireScope(AutomationScopes.Read).WithName("GetWorkflowRun");
        group.MapPost("/runs/{id:guid}/cancel", CancelRunAsync).RequireScope(AutomationScopes.Write).WithName("CancelWorkflowRun");

        endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/workflows", "Automation")
            .MapPost("", StartAsync).RequireScope(AutomationScopes.Write).WithName("StartWorkflow");

        var me = endpoints.MapV1Group("me/approvals", "Automation");
        me.MapGet("", ListApprovalsAsync).RequireScope(AutomationScopes.Read).WithName("ListMyApprovals");
        me.MapPost("/{id:guid}/decision", DecideAsync).RequireScope(AutomationScopes.Write).WithName("DecideApproval");

        var catalog = endpoints.MapV1Group("automation", "Automation");
        catalog.MapGet("/triggers", (TriggerCatalog triggers) => TypedResults.Ok(triggers.All.Select(t => new CatalogEntry(t.Key, t.Description)).ToList()))
            .RequireScope(AutomationScopes.Read).WithName("ListAutomationTriggers");
        catalog.MapGet("/actions", (ActionCatalog actions) => TypedResults.Ok(actions.All.Select(a => new CatalogEntry(a.Key, a.Description)).ToList()))
            .RequireScope(AutomationScopes.Read).WithName("ListAutomationActions");
    }

    // ---- Rules ------------------------------------------------------------------

    private static async Task<Results<Ok<List<RuleResponse>>, ProblemHttpResult>> ListRulesAsync(
        Guid workspaceId, IWorkspaceAccess workspaces, AutomationDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var rules = await db.Rules.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).OrderBy(r => r.Name).ToListAsync(ct);
        return TypedResults.Ok(rules.Select(ToResponse).ToList());
    }

    private static async Task<Results<Ok<RuleResponse>, ProblemHttpResult>> GetRuleAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, AutomationDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var rule = await db.Rules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.WorkspaceId == workspaceId, ct);
        if (rule is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, rule.Version);
        return TypedResults.Ok(ToResponse(rule));
    }

    private static async Task<Results<Created<RuleResponse>, ValidationProblem, ProblemHttpResult>> CreateRuleAsync(
        Guid workspaceId, RuleRequest request, IWorkspaceAccess workspaces, AutomationDbContext db, AutomationValidator validator,
        HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var (definition, invalid) = await ValidateAsync(workspaceId, request, validator, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (await db.Rules.AnyAsync(r => r.WorkspaceId == workspaceId && r.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A rule named '{name}' already exists in the workspace.");
        }

        var rule = new AutomationRule
        {
            Id = Ids.New(),
            WorkspaceId = workspaceId,
            Name = name,
            Enabled = request.Enabled,
            Trigger = definition!.Trigger.Type,
            Definition = DefinitionJson.Serialize(definition),
        };
        db.Rules.Add(rule);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, rule.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/automation/rules/{rule.Id}", ToResponse(rule));
    }

    private static async Task<Results<Ok<RuleResponse>, ValidationProblem, ProblemHttpResult>> ReplaceRuleAsync(
        Guid workspaceId, Guid id, RuleRequest request, IWorkspaceAccess workspaces, AutomationDbContext db, AutomationValidator validator,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id && r.WorkspaceId == workspaceId, ct);
        if (rule is null)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, rule, http) is { } precondition)
        {
            return precondition;
        }

        var (definition, invalid) = await ValidateAsync(workspaceId, request, validator, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (name != rule.Name && await db.Rules.AnyAsync(r => r.WorkspaceId == workspaceId && r.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A rule named '{name}' already exists in the workspace.");
        }

        rule.Name = name;
        rule.Enabled = request.Enabled;
        rule.Trigger = definition!.Trigger.Type;
        rule.Definition = DefinitionJson.Serialize(definition);
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        ETags.Set(response, rule.Version);
        return TypedResults.Ok(ToResponse(rule));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteRuleAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, AutomationDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id && r.WorkspaceId == workspaceId, ct);
        if (rule is null)
        {
            return ApiErrors.NotFound();
        }

        db.RuleRuns.RemoveRange(await db.RuleRuns.Where(r => r.RuleId == id).ToListAsync(ct));
        db.Rules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Runs of the rule, newest first (keyset paging with <c>$top</c>/<c>$skiptoken</c>).</summary>
    private static async Task<Results<Ok<Page<RuleRunResponse>>, ProblemHttpResult>> ListRuleRunsAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, AutomationDbContext db, HttpRequest http, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        if (!await db.Rules.AnyAsync(r => r.Id == id && r.WorkspaceId == workspaceId, ct))
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.From(http);
        var query = db.RuleRuns.AsNoTracking().Where(r => r.RuleId == id);
        if (page.After is { } after)
        {
            query = query.Where(r => r.Id.CompareTo(after) < 0);
        }

        var runs = await query.OrderByDescending(r => r.Id).Take(page.Top + 1)
            .Select(r => new RuleRunResponse(r.Id, r.EventId, r.ItemId, r.Status, r.Error, r.StartedAt, r.CompletedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(Page.Create(runs, page, http, r => r.Id));
    }

    private static async Task<(RuleDefinition? Definition, ValidationProblem? Problem)> ValidateAsync(
        Guid workspaceId, RuleRequest request, AutomationValidator validator, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return (null, invalid);
        }

        var definition = new RuleDefinition(request.Trigger!, string.IsNullOrWhiteSpace(request.Condition) ? null : request.Condition.Trim(), request.Actions ?? []);
        var errors = request.Trigger is null ? ["A trigger is required."] : await validator.ValidateRuleAsync(workspaceId, definition, ct);
        return errors.Count == 0 ? (definition, null) : (null, ApiErrors.Validation(new Dictionary<string, string[]> { ["rule"] = [.. errors] }));
    }

    private static RuleResponse ToResponse(AutomationRule rule)
    {
        var definition = DefinitionJson.Deserialize<RuleDefinition>(rule.Definition);
        return new RuleResponse(rule.Id, rule.WorkspaceId, rule.Name, rule.Enabled, definition.Trigger, definition.Condition, definition.Actions, rule.CreatedAt, rule.UpdatedAt);
    }

    // ---- Workflows ----------------------------------------------------------------

    private static async Task<Results<Ok<List<WorkflowResponse>>, ProblemHttpResult>> ListWorkflowsAsync(
        Guid workspaceId, IWorkspaceAccess workspaces, AutomationDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var workflows = await db.Workflows.AsNoTracking().Where(w => w.WorkspaceId == workspaceId).OrderBy(w => w.Name).ToListAsync(ct);
        var result = new List<WorkflowResponse>();
        foreach (var workflow in workflows)
        {
            result.Add(await ToResponseAsync(db, workflow, ct));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<WorkflowResponse>, ProblemHttpResult>> GetWorkflowAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, AutomationDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id && w.WorkspaceId == workspaceId, ct);
        if (workflow is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, workflow.Version);
        return TypedResults.Ok(await ToResponseAsync(db, workflow, ct));
    }

    private static async Task<Results<Created<WorkflowResponse>, ValidationProblem, ProblemHttpResult>> CreateWorkflowAsync(
        Guid workspaceId, WorkflowRequest request, IWorkspaceAccess workspaces, AutomationDbContext db, AutomationValidator validator,
        HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        if (Validate(request, validator) is { } invalid)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (await db.Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A workflow named '{name}' already exists in the workspace.");
        }

        var workflow = WorkflowWriter.Create(db, workspaceId, name, request.Description, request.Enabled, new WorkflowSteps(request.Steps!));
        await db.SaveChangesAsync(ct);
        ETags.Set(response, workflow.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/automation/workflows/{workflow.Id}", await ToResponseAsync(db, workflow, ct));
    }

    /// <summary>Replaces the workflow; changed steps become a new version (running runs keep theirs).</summary>
    private static async Task<Results<Ok<WorkflowResponse>, ValidationProblem, ProblemHttpResult>> ReplaceWorkflowAsync(
        Guid workspaceId, Guid id, WorkflowRequest request, IWorkspaceAccess workspaces, AutomationDbContext db, AutomationValidator validator,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var workflow = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id && w.WorkspaceId == workspaceId, ct);
        if (workflow is null)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, workflow, http) is { } precondition)
        {
            return precondition;
        }

        if (Validate(request, validator) is { } invalid)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (name != workflow.Name && await db.Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A workflow named '{name}' already exists in the workspace.");
        }

        workflow.Name = name;
        workflow.Description = request.Description;
        workflow.Enabled = request.Enabled;
        await WorkflowWriter.SetStepsAsync(db, workflow, new WorkflowSteps(request.Steps!), ct);
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        ETags.Set(response, workflow.Version);
        return TypedResults.Ok(await ToResponseAsync(db, workflow, ct));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteWorkflowAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, AutomationDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var workflow = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id && w.WorkspaceId == workspaceId, ct);
        if (workflow is null)
        {
            return ApiErrors.NotFound();
        }

        if (await db.Runs.AnyAsync(r => r.DefinitionId == id && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting), ct))
        {
            return ApiErrors.Conflict("workflowRunning", "The workflow has active runs; cancel them or disable the workflow.");
        }

        db.WorkflowVersions.RemoveRange(await db.WorkflowVersions.Where(v => v.DefinitionId == id).ToListAsync(ct));
        db.Workflows.Remove(workflow);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static ValidationProblem? Validate(WorkflowRequest request, AutomationValidator validator)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var errors = validator.ValidateWorkflow(new WorkflowSteps(request.Steps ?? []));
        return errors.Count == 0 ? null : ApiErrors.Validation(new Dictionary<string, string[]> { ["steps"] = [.. errors] });
    }

    private static async Task<WorkflowResponse> ToResponseAsync(AutomationDbContext db, WorkflowDefinition workflow, CancellationToken ct)
    {
        var version = await db.WorkflowVersions.AsNoTracking().FirstAsync(v => v.DefinitionId == workflow.Id && v.Number == workflow.CurrentVersion, ct);
        return new WorkflowResponse(workflow.Id, workflow.WorkspaceId, workflow.Name, workflow.Description, workflow.Enabled, workflow.CurrentVersion,
            DefinitionJson.Deserialize<WorkflowSteps>(version.Definition).Steps, workflow.CreatedAt, workflow.UpdatedAt);
    }

    // ---- Runs ------------------------------------------------------------------------

    /// <summary>Starts a workflow of the workspace on the item (Contribute on the item is required).</summary>
    private static async Task<Results<Created<RunResponse>, ValidationProblem, ProblemHttpResult>> StartAsync(
        Guid workspaceId, Guid listId, Guid itemId, StartWorkflowRequest request, IListItemStore items, WorkflowStarter starter,
        AutomationDbContext db, ICurrentUser user, CancellationToken ct)
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

        var (run, error) = await starter.StartAsync(new AutomationItem(workspaceId, listId, itemId), request.Workflow, user.UserId, ct);
        if (run is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["workflow"] = [error!] });
        }

        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/automation/runs/{run.Id}", await ToResponseAsync(db, run, ct));
    }

    /// <summary>Runs in the workspace, newest first; filter by <c>itemId</c> or <c>workflowId</c>.</summary>
    private static async Task<Results<Ok<Page<RunResponse>>, ProblemHttpResult>> ListRunsAsync(
        Guid workspaceId, Guid? itemId, Guid? workflowId, IWorkspaceAccess workspaces, AutomationDbContext db, HttpRequest http, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var page = PageRequest.From(http);
        var query = db.Runs.AsNoTracking().Where(r => r.WorkspaceId == workspaceId);
        query = itemId is { } item ? query.Where(r => r.ItemId == item) : query;
        query = workflowId is { } workflow ? query.Where(r => r.DefinitionId == workflow) : query;
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
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, AutomationDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.WorkspaceId == workspaceId, ct);
        return run is null ? ApiErrors.NotFound() : TypedResults.Ok(await ToResponseAsync(db, run, ct));
    }

    private static async Task<Results<Ok<RunResponse>, ProblemHttpResult>> CancelRunAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, AutomationDbContext db, ApprovalService approvals, CancellationToken ct)
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

    private static async Task<RunResponse> ToResponseAsync(AutomationDbContext db, WorkflowRun run, CancellationToken ct)
    {
        var name = await db.Workflows.AsNoTracking().Where(w => w.Id == run.DefinitionId).Select(w => w.Name).FirstOrDefaultAsync(ct);
        return new RunResponse(run.Id, run.DefinitionId, name, run.DefinitionVersion, run.WorkspaceId, run.ListId, run.ItemId, run.Status,
            DefinitionJson.Deserialize<Dictionary<string, string>>(run.Outcomes), JsonNode.Parse(run.Log) as JsonArray ?? [], run.Error, run.StartedBy,
            run.StartedAt, run.CompletedAt);
    }

    // ---- Approvals ---------------------------------------------------------------------

    /// <summary>Approvals assigned to the caller, newest first; <c>status</c> filters (default: pending).</summary>
    private static async Task<Ok<Page<ApprovalResponse>>> ListApprovalsAsync(
        ApprovalStatus? status, AutomationDbContext db, ICurrentUser user, HttpRequest http, CancellationToken ct)
    {
        var page = PageRequest.From(http);
        var wanted = status ?? ApprovalStatus.Pending;
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
        Guid id, DecisionRequest request, ApprovalService approvals, AutomationDbContext db, ICurrentUser user, CancellationToken ct)
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
            case ApprovalService.DecisionResult.NotFound:
                return ApiErrors.NotFound();
            case ApprovalService.DecisionResult.AlreadyDecided:
                return ApiErrors.Conflict("alreadyDecided", "The approval was already decided or cancelled.");
        }

        return TypedResults.Ok(ApprovalResponse.From(await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == id, ct)));
    }

    // ---- Helpers -----------------------------------------------------------------------

    private static async Task<ProblemHttpResult?> AccessAsync(IWorkspaceAccess workspaces, Guid workspaceId, WorkspaceAccessLevel needed, CancellationToken ct)
    {
        var level = await workspaces.GetPermissionAsync(workspaceId, ct);
        return level == WorkspaceAccessLevel.None ? ApiErrors.NotFound()
            : level < needed ? ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "Managing the workspace is required.")
            : null;
    }

    private static ProblemHttpResult? CheckIfMatch<T>(AutomationDbContext db, T entity, HttpRequest http)
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

    private static async Task<ProblemHttpResult?> SaveAsync(AutomationDbContext db, CancellationToken ct)
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
    public static WorkflowDefinition Create(AutomationDbContext db, Guid workspaceId, string name, string? description, bool enabled, WorkflowSteps steps)
    {
        var workflow = new WorkflowDefinition { Id = Ids.New(), WorkspaceId = workspaceId, Name = name, Description = description, Enabled = enabled, CurrentVersion = 1 };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(new WorkflowDefinitionVersion { Id = Ids.New(), DefinitionId = workflow.Id, Number = 1, Definition = DefinitionJson.Serialize(steps) });
        return workflow;
    }

    /// <summary>Adds a version when the steps changed; returns whether they did.</summary>
    public static async Task<bool> SetStepsAsync(AutomationDbContext db, WorkflowDefinition workflow, WorkflowSteps steps, CancellationToken ct)
    {
        var json = DefinitionJson.Serialize(steps);
        var current = await db.WorkflowVersions.AsNoTracking().FirstAsync(v => v.DefinitionId == workflow.Id && v.Number == workflow.CurrentVersion, ct);
        if (current.Definition == json)
        {
            return false;
        }

        workflow.CurrentVersion++;
        db.WorkflowVersions.Add(new WorkflowDefinitionVersion { Id = Ids.New(), DefinitionId = workflow.Id, Number = workflow.CurrentVersion, Definition = json });
        return true;
    }
}

/// <summary>Validation of rules and workflows against the catalog and the workspace's lists.</summary>
internal sealed class AutomationValidator(TriggerCatalog triggers, ActionCatalog actions, IListItemStore items)
{
    public async Task<List<string>> ValidateRuleAsync(Guid workspaceId, RuleDefinition rule, CancellationToken ct)
    {
        var errors = Definitions.ValidateRule(rule, triggers.Keys, actions);
        if (errors.Count > 0 || rule.Trigger.List is not { } listName)
        {
            return errors;
        }

        var list = (await items.AsSystem().GetListsAsync(workspaceId, null, ct)).FirstOrDefault(l => l.Name == listName);
        if (list is null)
        {
            errors.Add($"The list '{listName}' does not exist in the workspace.");
            return errors;
        }

        if (rule.Trigger.ContentType is { } type && !list.ContentTypes.Any(c => c.Name == type || c.Key == type))
        {
            errors.Add($"The list '{listName}' has no content type '{type}'.");
        }

        if (rule.Condition is { } condition)
        {
            var (_, error) = await items.AsSystem().QueryAsync(workspaceId, list.Id, new ListItemQuery($"id eq {Guid.Empty} and ({condition})", Top: 1), ct);
            if (error is not null)
            {
                errors.Add($"condition: {error}");
            }
        }

        return errors;
    }

    public List<string> ValidateWorkflow(WorkflowSteps workflow) => Definitions.ValidateWorkflow(workflow, actions);
}
