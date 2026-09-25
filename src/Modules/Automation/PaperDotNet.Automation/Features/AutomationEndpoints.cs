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

public sealed record AutomationRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(2000)] string? Description,
    AutomationTrigger? Trigger,
    [property: StringLength(4000)] string? Condition,
    IReadOnlyList<AutomationStep>? Steps,
    bool Enabled = true);

/// <summary>An automation with the definition of its current <c>version</c> (runs keep the version they started with).</summary>
public sealed record AutomationResponse(
    Guid Id, Guid WorkspaceId, string Name, string? Description, bool Enabled, int Version, AutomationTrigger Trigger, string? Condition,
    IReadOnlyList<AutomationStep> Steps, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

public sealed record StartAutomationRequest([property: Required] string Automation);

public sealed record RunResponse(
    Guid Id, Guid AutomationId, string? Automation, int AutomationVersion, Guid WorkspaceId, Guid? ListId, Guid? ItemId, Guid? EventId,
    RunStatus Status, IReadOnlyDictionary<string, string> Outcomes, JsonArray Log, string? Error, Guid? StartedBy, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

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
/// Automation API (EVT-07…09, DOC-14): automations of a workspace, their runs, approvals and the catalog of
/// triggers and actions. Reading needs Read access to the workspace; changes need Manage.
/// </summary>
internal static class AutomationEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("workspaces/{workspaceId:guid}/automations", "Automation");
        group.MapGet("", ListAsync).RequireScope(AutomationScopes.Read).WithName("ListAutomations");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(AutomationScopes.Read).WithName("GetAutomation");
        group.MapPost("", CreateAsync).RequireScope(AutomationScopes.Write).WithName("CreateAutomation");
        group.MapPut("/{id:guid}", ReplaceAsync).RequireScope(AutomationScopes.Write).WithName("ReplaceAutomation");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireScope(AutomationScopes.Write).WithName("DeleteAutomation");
        group.MapGet("/runs", ListRunsAsync).RequireScope(AutomationScopes.Read).WithName("ListAutomationRuns");
        group.MapGet("/runs/{id:guid}", GetRunAsync).RequireScope(AutomationScopes.Read).WithName("GetAutomationRun");
        group.MapPost("/runs/{id:guid}/cancel", CancelRunAsync).RequireScope(AutomationScopes.Write).WithName("CancelAutomationRun");

        endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/automations", "Automation")
            .MapPost("", StartAsync).RequireScope(AutomationScopes.Write).WithName("StartAutomation");

        var me = endpoints.MapV1Group("me/approvals", "Automation");
        me.MapGet("", ListApprovalsAsync).RequireScope(AutomationScopes.Read).WithName("ListMyApprovals");
        me.MapPost("/{id:guid}/decision", DecideAsync).RequireScope(AutomationScopes.Write).WithName("DecideApproval");

        var catalog = endpoints.MapV1Group("automation", "Automation");
        catalog.MapGet("/triggers", (TriggerCatalog triggers) => TypedResults.Ok(triggers.All.Select(t => new CatalogEntry(t.Key, t.Description)).ToList()))
            .RequireScope(AutomationScopes.Read).WithName("ListAutomationTriggers");
        catalog.MapGet("/actions", (ActionCatalog actions) => TypedResults.Ok(actions.All.Select(a => new CatalogEntry(a.Key, a.Description)).ToList()))
            .RequireScope(AutomationScopes.Read).WithName("ListAutomationActions");
    }

    // ---- Automations ----------------------------------------------------------------

    private static async Task<Results<Ok<List<AutomationResponse>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, IWorkspaceAccess workspaces, AutomationDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var automations = await db.Automations.AsNoTracking().Where(a => a.WorkspaceId == workspaceId).OrderBy(a => a.Name).ToListAsync(ct);
        var result = new List<AutomationResponse>();
        foreach (var automation in automations)
        {
            result.Add(await ToResponseAsync(db, automation, ct));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<AutomationResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, AutomationDbContext db, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var automation = await db.Automations.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == workspaceId, ct);
        if (automation is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, automation.Version);
        return TypedResults.Ok(await ToResponseAsync(db, automation, ct));
    }

    private static async Task<Results<Created<AutomationResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, AutomationRequest request, IWorkspaceAccess workspaces, AutomationDbContext db, AutomationValidator validator,
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
        if (await db.Automations.AnyAsync(a => a.WorkspaceId == workspaceId && a.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"An automation named '{name}' already exists in the workspace.");
        }

        var automation = AutomationWriter.Create(db, workspaceId, name, request.Description, request.Enabled, spec!);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, automation.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/automations/{automation.Id}", await ToResponseAsync(db, automation, ct));
    }

    /// <summary>Replaces the automation; a changed definition becomes a new version (running runs keep theirs).</summary>
    private static async Task<Results<Ok<AutomationResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid workspaceId, Guid id, AutomationRequest request, IWorkspaceAccess workspaces, AutomationDbContext db, AutomationValidator validator,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var automation = await db.Automations.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == workspaceId, ct);
        if (automation is null)
        {
            return ApiErrors.NotFound();
        }

        if (CheckIfMatch(db, automation, http) is { } precondition)
        {
            return precondition;
        }

        var (spec, invalid) = await ValidateAsync(workspaceId, request, validator, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (name != automation.Name && await db.Automations.AnyAsync(a => a.WorkspaceId == workspaceId && a.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"An automation named '{name}' already exists in the workspace.");
        }

        automation.Name = name;
        automation.Description = request.Description;
        automation.Enabled = request.Enabled;
        await AutomationWriter.SetSpecAsync(db, automation, spec!, ct);
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        ETags.Set(response, automation.Version);
        return TypedResults.Ok(await ToResponseAsync(db, automation, ct));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid id, IWorkspaceAccess workspaces, AutomationDbContext db, CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Manage, ct) is { } denied)
        {
            return denied;
        }

        var automation = await db.Automations.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == workspaceId, ct);
        if (automation is null)
        {
            return ApiErrors.NotFound();
        }

        if (await db.Runs.AnyAsync(r => r.AutomationId == id && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting), ct))
        {
            return ApiErrors.Conflict("automationRunning", "The automation has active runs; cancel them or disable the automation.");
        }

        var runs = db.Runs.Where(r => r.AutomationId == id).Select(r => r.Id);
        await db.Approvals.Where(a => runs.Contains(a.RunId)).ExecuteDeleteAsync(ct);
        await db.Runs.Where(r => r.AutomationId == id).ExecuteDeleteAsync(ct);
        await db.Versions.Where(v => v.AutomationId == id).ExecuteDeleteAsync(ct);
        db.Automations.Remove(automation);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<(AutomationSpec? Spec, ValidationProblem? Problem)> ValidateAsync(
        Guid workspaceId, AutomationRequest request, AutomationValidator validator, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return (null, invalid);
        }

        var spec = new AutomationSpec(request.Trigger!, string.IsNullOrWhiteSpace(request.Condition) ? null : request.Condition.Trim(), request.Steps ?? []);
        var errors = await validator.ValidateAsync(workspaceId, spec, ct);
        return errors.Count == 0 ? (spec, null) : (null, ApiErrors.Validation(new Dictionary<string, string[]> { ["automation"] = [.. errors] }));
    }

    private static async Task<AutomationResponse> ToResponseAsync(AutomationDbContext db, AutomationDefinition automation, CancellationToken ct)
    {
        var version = await db.Versions.AsNoTracking().FirstAsync(v => v.AutomationId == automation.Id && v.Number == automation.CurrentVersion, ct);
        var spec = DefinitionJson.Deserialize<AutomationSpec>(version.Definition);
        return new AutomationResponse(automation.Id, automation.WorkspaceId, automation.Name, automation.Description, automation.Enabled,
            automation.CurrentVersion, spec.Trigger, spec.Condition, spec.Steps, automation.CreatedAt, automation.UpdatedAt)
        { ETag = ETags.From(automation.Version) };
    }

    // ---- Runs ------------------------------------------------------------------------

    /// <summary>Starts an automation with the <c>manual</c> trigger on the item (Contribute on the item is required).</summary>
    private static async Task<Results<Created<RunResponse>, ValidationProblem, ProblemHttpResult>> StartAsync(
        Guid workspaceId, Guid listId, Guid itemId, StartAutomationRequest request, IListItemStore items, AutomationStarter starter,
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

        var (run, error) = await starter.StartManualAsync(new AutomationItem(workspaceId, listId, itemId), request.Automation.Trim(), user.UserId, ct);
        if (run is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["automation"] = [error!] });
        }

        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/automations/runs/{run.Id}", await ToResponseAsync(db, run, ct));
    }

    /// <summary>Runs in the workspace, newest first; filter by <c>automationId</c>, <c>itemId</c> or <c>status</c>.</summary>
    private static async Task<Results<Ok<Page<RunResponse>>, ProblemHttpResult>> ListRunsAsync(
        Guid workspaceId, Guid? automationId, Guid? itemId, RunStatus? status, IWorkspaceAccess workspaces, AutomationDbContext db, HttpRequest http,
        CancellationToken ct)
    {
        if (await AccessAsync(workspaces, workspaceId, WorkspaceAccessLevel.Read, ct) is { } denied)
        {
            return denied;
        }

        var page = PageRequest.From(http);
        var query = db.Runs.AsNoTracking().Where(r => r.WorkspaceId == workspaceId);
        query = itemId is { } item ? query.Where(r => r.ItemId == item) : query;
        query = automationId is { } automation ? query.Where(r => r.AutomationId == automation) : query;
        query = status is { } wanted ? query.Where(r => r.Status == wanted) : query;
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

    private static async Task<RunResponse> ToResponseAsync(AutomationDbContext db, AutomationRun run, CancellationToken ct)
    {
        var name = await db.Automations.AsNoTracking().Where(a => a.Id == run.AutomationId).Select(a => a.Name).FirstOrDefaultAsync(ct);
        return new RunResponse(run.Id, run.AutomationId, name, run.AutomationVersion, run.WorkspaceId, run.ListId, run.ItemId, run.EventId, run.Status,
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

/// <summary>Creates automations and their versions (shared by the API and templates).</summary>
internal static class AutomationWriter
{
    public static AutomationDefinition Create(AutomationDbContext db, Guid workspaceId, string name, string? description, bool enabled, AutomationSpec spec)
    {
        var automation = new AutomationDefinition
        {
            Id = Ids.New(),
            WorkspaceId = workspaceId,
            Name = name,
            Description = description,
            Enabled = enabled,
            Trigger = spec.Trigger.Type,
            CurrentVersion = 1,
        };
        db.Automations.Add(automation);
        db.Versions.Add(new AutomationVersion { Id = Ids.New(), AutomationId = automation.Id, Number = 1, Definition = DefinitionJson.Serialize(spec) });
        return automation;
    }

    /// <summary>Adds a version when the definition changed; returns whether it did.</summary>
    public static async Task<bool> SetSpecAsync(AutomationDbContext db, AutomationDefinition automation, AutomationSpec spec, CancellationToken ct)
    {
        var json = DefinitionJson.Serialize(spec);
        var current = await db.Versions.AsNoTracking().FirstAsync(v => v.AutomationId == automation.Id && v.Number == automation.CurrentVersion, ct);
        if (current.Definition == json)
        {
            return false;
        }

        automation.CurrentVersion++;
        automation.Trigger = spec.Trigger.Type;
        db.Versions.Add(new AutomationVersion { Id = Ids.New(), AutomationId = automation.Id, Number = automation.CurrentVersion, Definition = json });
        return true;
    }
}

/// <summary>Validation of automations against the catalog and the workspace's lists.</summary>
internal sealed class AutomationValidator(TriggerCatalog triggers, ActionCatalog actions, IListItemStore items)
{
    public async Task<List<string>> ValidateAsync(Guid workspaceId, AutomationSpec spec, CancellationToken ct)
    {
        var errors = Definitions.Validate(spec, triggers.Keys, actions);
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
