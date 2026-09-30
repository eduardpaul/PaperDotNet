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


namespace PaperDotNet.Workflows.Features;

public sealed record WorkflowDto(
    Guid Id,
    string Name,
    string? Description,
    bool Enabled,
    int Version,
    JsonObject Definition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("@odata.etag")] string ETag);

public sealed record CreateWorkflowRequest(string Name, string? Description, bool? Enabled, JsonObject Definition);

public sealed record UpdateWorkflowRequest(string? Name, string? Description, bool? Enabled, JsonObject? Definition);

public sealed record StartRunRequest(Guid? ListId, Guid? ItemId, JsonObject? Inputs);

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

/// <summary>Workflows (ADR-0036) in the AOT core. Queries copy their arguments into locals (ADR-0039).</summary>
internal static class WorkflowEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0").WithTags("Workflows");
        group.MapGet("/workflows", ListAsync).RequireScope(Scopes.WorkflowsManage).WithName("ListWorkflows");
        group.MapPost("/workflows", CreateAsync).RequireScope(Scopes.WorkflowsManage).WithName("CreateWorkflow");
        group.MapGet("/workflows/activities", Activities).RequireScope(Scopes.WorkflowsManage).WithName("ListWorkflowActivities");
        group.MapGet("/workflows/{workflowId:guid}", GetAsync).RequireScope(Scopes.WorkflowsManage).WithName("GetWorkflow");
        group.MapPatch("/workflows/{workflowId:guid}", UpdateAsync).RequireScope(Scopes.WorkflowsManage).WithName("UpdateWorkflow");
        group.MapDelete("/workflows/{workflowId:guid}", DeleteAsync).RequireScope(Scopes.WorkflowsManage).WithName("DeleteWorkflow");
        group.MapPost("/workflows/{workflowId:guid}/runs", StartAsync).RequireScope(Scopes.WorkflowsManage).WithName("StartWorkflowRun");
        group.MapGet("/workflows/{workflowId:guid}/runs", ListRunsAsync).RequireScope(Scopes.WorkflowsManage).WithName("ListWorkflowRuns");
        group.MapGet("/workflow-runs/{runId:guid}", GetRunAsync).RequireScope(Scopes.WorkflowsManage).WithName("GetWorkflowRun");
    }

    private static WorkflowDto ToDto(WorkflowDefinition workflow, WorkflowVersion version) =>
        new(workflow.Id, workflow.Name, workflow.Description, workflow.Enabled, workflow.CurrentVersion, JsonNode.Parse(version.Definition)!.AsObject(),
            workflow.CreatedAt, workflow.UpdatedAt, ETags.From(workflow.Version));

    private static RunDto ToDto(WorkflowRun run) =>
        new(run.Id, run.WorkflowId, run.WorkflowVersion, run.Status, run.Trigger, run.Node, run.ListId, run.ItemId,
            JsonNode.Parse(run.Outputs), JsonNode.Parse(run.Variables), JsonNode.Parse(run.Log), run.Error, run.FailedNode, run.StartedAt, run.CompletedAt);

    private static Task<WorkflowDefinition?> FindAsync(WorkflowsDbContext database, Guid tenantId, Guid workflowId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = workflowId;
        var ct = cancellationToken;
        return db.Workflows.Where(w => w.TenantId == tenant && w.Id == id).FirstOrDefaultAsync(ct);
    }

    private static Task<bool> NameTakenAsync(WorkflowsDbContext database, Guid tenantId, string workflowName, Guid exceptId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var name = workflowName;
        var except = exceptId;
        var ct = cancellationToken;
        return db.Workflows.AnyAsync(w => w.TenantId == tenant && w.Name == name && w.Id != except, ct);
    }

    /// <summary>The definition checked and normalized, or the validation problem.</summary>
    private static (WorkflowSpec? Spec, ValidationProblem? Problem) Check(JsonObject? definition, IEnumerable<IWorkflowActivity> activities)
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

        var errors = DefinitionValidator.Validate(spec, activities.ToDictionary(a => a.Key, StringComparer.Ordinal));
        return errors.Count > 0 ? (null, ApiErrors.Validation(new Dictionary<string, string[]> { ["definition"] = [.. errors] })) : (spec, null);
    }

    private static string TriggerTypes(WorkflowSpec spec) => $",{string.Join(',', spec.AllTriggers.Select(t => t.Type).Distinct(StringComparer.Ordinal))},";

    private static Ok<IReadOnlyList<ActivityDto>> Activities(IEnumerable<IWorkflowActivity> activities) =>
        TypedResults.Ok<IReadOnlyList<ActivityDto>>([
            .. FlowActivities.All.Select(a => new ActivityDto(a, "Flow activity (see ADR-0036).", [.. FlowActivities.Ports(a, null)])),
            .. activities.Select(a => new ActivityDto(a.Key, a.Description, ["done", "error", .. a.Outcomes])),
        ]);

    private static async Task<Ok<Page<WorkflowDto>>> ListAsync(
        HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller, WorkflowsDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var workflows = page.After is { } after
            ? await db.Workflows.Where(w => w.TenantId == tenant && w.Id.CompareTo(after) > 0).OrderBy(w => w.Id).Take(take).ToListAsync(ct)
            : await db.Workflows.Where(w => w.TenantId == tenant).OrderBy(w => w.Id).Take(take).ToListAsync(ct);
        var dtos = new List<WorkflowDto>();
        foreach (var workflow in workflows)
        {
            dtos.Add(ToDto(workflow, (await WorkflowVersions.FindAsync(db, tenant, workflow.Id, workflow.CurrentVersion, ct))!));
        }

        return TypedResults.Ok(Page.Create(dtos, page, request, w => w.Id));
    }

    private static async Task<Results<Created<WorkflowDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateWorkflowRequest body, Caller caller, WorkflowsDbContext db, IEnumerable<IWorkflowActivity> activities, TimeProvider time, CancellationToken cancellationToken)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200)
        {
            return ApiErrors.Validation("name", "A name of 1 to 200 characters is required.");
        }

        var (spec, problem) = Check(body.Definition, activities);
        if (problem is not null)
        {
            return problem;
        }

        if (await NameTakenAsync(db, caller.TenantId, name, Guid.Empty, cancellationToken))
        {
            return ApiErrors.Conflict("nameTaken", "A workflow with this name exists.");
        }

        var workflow = new WorkflowDefinition
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            Name = name,
            Description = body.Description,
            Enabled = body.Enabled ?? true,
            CurrentVersion = 1,
            TriggerTypes = TriggerTypes(spec!),
        };
        var version = new WorkflowVersion { Id = Ids.New(), TenantId = caller.TenantId, WorkflowId = workflow.Id, Number = 1, Definition = WorkflowJson.Serialize(spec!), CreatedAt = time.GetUtcNow() };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v1.0/workflows/{workflow.Id}", ToDto(workflow, version));
    }

    private static async Task<Results<Ok<WorkflowDto>, ProblemHttpResult>> GetAsync(Guid workflowId, HttpResponse response, Caller caller, WorkflowsDbContext db, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, caller.TenantId, workflowId, cancellationToken) is not { } workflow
            || await WorkflowVersions.FindAsync(db, caller.TenantId, workflowId, workflow.CurrentVersion, cancellationToken) is not { } version)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, workflow.Version);
        return TypedResults.Ok(ToDto(workflow, version));
    }

    private static async Task<Results<Ok<WorkflowDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid workflowId, UpdateWorkflowRequest body, HttpContext http, Caller caller, WorkflowsDbContext db, IEnumerable<IWorkflowActivity> activities, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!ETags.TryGetIfMatch(http.Request, out var etag))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (await FindAsync(db, caller.TenantId, workflowId, cancellationToken) is not { } workflow)
        {
            return ApiErrors.NotFound();
        }

        if (workflow.Version != etag)
        {
            return ApiErrors.PreconditionFailed();
        }

        if (body.Name is { } newName)
        {
            var name = newName.Trim();
            if (name.Length is 0 or > 200)
            {
                return ApiErrors.Validation("name", "A name of 1 to 200 characters is required.");
            }

            if (await NameTakenAsync(db, caller.TenantId, name, workflow.Id, cancellationToken))
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
            var (spec, problem) = Check(body.Definition, activities);
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

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid workflowId, Caller caller, WorkflowsDbContext db, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, caller.TenantId, workflowId, cancellationToken) is not { } workflow)
        {
            return ApiErrors.NotFound();
        }

        // Versions and runs go with the workflow (foreign key cascade).
        db.Workflows.Remove(workflow);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Accepted<RunDto>, ValidationProblem, ProblemHttpResult>> StartAsync(
        Guid workflowId, StartRunRequest body, Caller caller, WorkflowsDbContext db, WorkflowStarter starter, IListItemStore items, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, caller.TenantId, workflowId, cancellationToken) is not { } workflow
            || await WorkflowVersions.FindAsync(db, caller.TenantId, workflowId, workflow.CurrentVersion, cancellationToken) is not { } version)
        {
            return ApiErrors.NotFound();
        }

        var spec = WorkflowJson.Deserialize(version.Definition);
        if (!workflow.Enabled || !spec.AllTriggers.Any(t => t.Type == WorkflowTriggers.Manual))
        {
            return ApiErrors.Conflict("notManual", "The workflow is turned off or has no manual trigger.");
        }

        if (body.ItemId is { } itemId && (body.ListId is not { } listId || await items.GetAsync(caller.TenantId, listId, itemId, cancellationToken) is null))
        {
            return ApiErrors.Validation("itemId", "The item was not found in the list (give listId and itemId).");
        }

        var run = await starter.StartManualAsync(workflow, spec, body.ItemId is null ? null : body.ListId, body.ItemId, body.Inputs, caller.UserId, cancellationToken);
        return TypedResults.Accepted($"/v1.0/workflow-runs/{run.Id}", ToDto(run));
    }

    private static async Task<Ok<Page<RunDto>>> ListRunsAsync(
        Guid workflowId, HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Caller caller, WorkflowsDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var workflow = workflowId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var runs = page.After is { } before
            ? await db.WorkflowRuns.Where(r => r.TenantId == tenant && r.WorkflowId == workflow && r.Id.CompareTo(before) < 0).OrderByDescending(r => r.Id).Take(take).ToListAsync(ct)
            : await db.WorkflowRuns.Where(r => r.TenantId == tenant && r.WorkflowId == workflow).OrderByDescending(r => r.Id).Take(take).ToListAsync(ct);
        return TypedResults.Ok(Page.Create([.. runs.Select(ToDto)], page, request, r => r.Id));
    }

    private static async Task<Results<Ok<RunDto>, ProblemHttpResult>> GetRunAsync(Guid runId, Caller caller, WorkflowsDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var id = runId;
        var ct = cancellationToken;
        return await db.WorkflowRuns.Where(r => r.TenantId == tenant && r.Id == id).FirstOrDefaultAsync(ct) is { } run
            ? TypedResults.Ok(ToDto(run))
            : ApiErrors.NotFound();
    }
}
