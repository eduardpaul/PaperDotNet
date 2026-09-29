using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Built-in workflows (EVT-12, ADR-0036): the catalog of what modules ship (<see cref="IWorkflowDefinitionProvider"/>),
/// and their state in a workspace. An enabled built-in workflow is a workflow row with a <c>BuiltInKey</c>: its
/// definition is the release's with the workspace's parameter values filled in, it cannot be edited (copy it instead),
/// and a new release's definition becomes a new version (<see cref="BuiltInSyncJob"/>).
/// </summary>
internal sealed partial class BuiltInWorkflows(
    IEnumerable<IWorkflowDefinitionProvider> providers, IServiceProvider services, WorkflowsDbContext db, WorkflowValidator validator)
{
    public IReadOnlyList<BuiltInWorkflow> All =>
        [.. providers.SelectMany(p => p.GetWorkflows()).DistinctBy(w => w.Key).OrderBy(w => w.Name, StringComparer.Ordinal)];

    public BuiltInWorkflow? Find(string key) => All.FirstOrDefault(w => w.Key == key);

    /// <summary>Whether the server has what the workflow needs.</summary>
    public bool IsAvailable(BuiltInWorkflow workflow) => workflow.Requires switch
    {
        null => true,
        BuiltInRequirements.Ai => services.GetService<IChatClient>() is not null,
        _ => false,
    };

    /// <summary>
    /// The definition with the parameter values filled in (missing values take the schema's <c>default</c>): the definition,
    /// the values used, or why they do not fit.
    /// </summary>
    public static (WorkflowSpec? Spec, JsonObject Values, string? Error) Resolve(BuiltInWorkflow workflow, JsonObject? parameters)
    {
        var values = parameters?.DeepClone().AsObject() ?? [];
        foreach (var (name, property) in workflow.Parameters?["properties"] as JsonObject ?? [])
        {
            if (values[name] is null && property?["default"] is { } fallback)
            {
                values[name] = fallback.DeepClone();
            }
        }

        foreach (var name in values.Where(v => v.Value is null).Select(v => v.Key).ToList())
        {
            values.Remove(name);
        }

        if (WorkflowInputs.Check(workflow.Parameters, values) is { } error)
        {
            return (null, values, error.Replace("input", "parameter", StringComparison.Ordinal));
        }

        var (spec, invalid) = DefinitionJson.TryParse<WorkflowSpec>(Fill(workflow.Definition, values)!.ToJsonString());
        return (spec, values, invalid is null ? null : $"The definition is not valid: {invalid}");
    }

    /// <summary>Why the workflow cannot run in the workspace with these values, or null.</summary>
    public async Task<(WorkflowSpec? Spec, JsonObject Values, List<string> Errors)> CheckAsync(
        Guid workspaceId, BuiltInWorkflow workflow, JsonObject? parameters, CancellationToken ct)
    {
        if (!IsAvailable(workflow))
        {
            return (null, parameters ?? [], [$"'{workflow.Name}' needs {workflow.Requires} to be configured on the server."]);
        }

        var (spec, values, error) = Resolve(workflow, parameters);
        if (error is not null)
        {
            return (null, values, [error]);
        }

        var errors = await validator.ValidateAsync(workspaceId, spec!, ct);
        return (errors.Count == 0 ? spec : null, values, errors);
    }

    /// <summary>The built-in workflow's row in the workspace (null when it was never enabled).</summary>
    public Task<WorkflowDefinition?> RowAsync(Guid workspaceId, string key, CancellationToken ct) =>
        db.Workflows.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.BuiltInKey == key, ct);

    public static JsonObject? Values(WorkflowDefinition? row) => row?.Parameters is { } json ? JsonNode.Parse(json) as JsonObject : null;

    /// <summary>
    /// Turns the built-in workflow on (with <paramref name="parameters"/>, else the ones it had) or off in the workspace.
    /// The row is added to the context, not saved. Errors when the values do not fit, or a workflow of the same name exists.
    /// </summary>
    public async Task<(WorkflowDefinition? Row, List<string> Errors, bool NameTaken)> SetAsync(
        Guid workspaceId, BuiltInWorkflow workflow, bool enabled, JsonObject? parameters, CancellationToken ct)
    {
        var row = await RowAsync(workspaceId, workflow.Key, ct);
        if (!enabled && parameters is null)
        {
            if (row is not null)
            {
                row.Enabled = false;
            }

            return (row, [], false);
        }

        var (spec, values, errors) = await CheckAsync(workspaceId, workflow, parameters ?? Values(row), ct);
        if (errors.Count > 0)
        {
            return (null, errors, false);
        }

        if (row is null)
        {
            if (await db.Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Name == workflow.Name, ct))
            {
                return (null, [$"A workflow named '{workflow.Name}' already exists in the workspace."], true);
            }

            row = WorkflowWriter.Create(db, workspaceId, workflow.Name, workflow.Description, enabled, spec!);
            row.BuiltInKey = workflow.Key;
        }
        else
        {
            await WorkflowWriter.SetSpecAsync(db, row, spec!, ct);
            row.Enabled = enabled;
            row.Description = workflow.Description;
        }

        row.Parameters = values.ToJsonString();
        return (row, [], false);
    }

    /// <summary>
    /// A workflow of the workspace's own made from the built-in one (to change it), named <paramref name="name"/>; the
    /// built-in one is turned off there. Added to the context, not saved.
    /// </summary>
    public async Task<(WorkflowDefinition? Copy, List<string> Errors, bool NameTaken)> CopyAsync(
        Guid workspaceId, BuiltInWorkflow workflow, string name, JsonObject? parameters, CancellationToken ct)
    {
        var row = await RowAsync(workspaceId, workflow.Key, ct);
        var (spec, _, errors) = await CheckAsync(workspaceId, workflow, parameters ?? Values(row), ct);
        if (errors.Count > 0)
        {
            return (null, errors, false);
        }

        if (await db.Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Name == name, ct))
        {
            return (null, [$"A workflow named '{name}' already exists in the workspace."], true);
        }

        var copy = WorkflowWriter.Create(db, workspaceId, name, workflow.Description, true, spec!);
        copy.CopiedFrom = workflow.Key;
        if (row is not null)
        {
            row.Enabled = false;
        }

        return (copy, [], false);
    }

    /// <summary>
    /// Brings the organization's built-in workflows up to the release: a changed definition becomes a new version; one the
    /// release no longer has is turned off; one that no longer fits its workspace (e.g. a list was renamed) keeps its version.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        foreach (var row in await db.Workflows.Where(w => w.BuiltInKey != null).ToListAsync(ct))
        {
            if (Find(row.BuiltInKey!) is not { } workflow)
            {
                row.Enabled = false;
                continue;
            }

            var (spec, _, errors) = await CheckAsync(row.WorkspaceId, workflow, Values(row), ct);
            if (errors.Count == 0)
            {
                await WorkflowWriter.SetSpecAsync(db, row, spec!, ct);
                row.Description = workflow.Description;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Replaces <c>{param:name}</c> in strings and property names of a definition.</summary>
    private static JsonNode? Fill(JsonNode? node, JsonObject values) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(Text(p.Key, values), Fill(p.Value, values)))),
        JsonArray array => new JsonArray([.. array.Select(v => Fill(v, values))]),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => Whole().Match(value.GetValue<string>()) is { Success: true } whole
            ? values[whole.Groups[1].Value]?.DeepClone()
            : JsonValue.Create(Text(value.GetValue<string>(), values)),
        _ => node?.DeepClone(),
    };

    private static string Text(string text, JsonObject values) => Parameter().Replace(text, m => values[m.Groups[1].Value] switch
    {
        null => string.Empty,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        var other => other.ToJsonString(),
    });

    [GeneratedRegex(@"^\{param:([A-Za-z0-9_]+)\}$")]
    private static partial Regex Whole();

    [GeneratedRegex(@"\{param:([A-Za-z0-9_]+)\}")]
    private static partial Regex Parameter();
}

/// <summary>Hourly: brings built-in workflows up to the running release (<see cref="BuiltInWorkflows.SyncAsync"/>).</summary>
internal sealed class BuiltInSyncJob(BuiltInWorkflows builtIns) : ITenantRecurringJob
{
    public const string Name = "workflows.builtIns";
    public const string Schedule = "41 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => builtIns.SyncAsync(cancellationToken);
}

/// <summary>The workflow module's own built-in workflows: templates people enable for a list.</summary>
internal sealed class WorkflowBuiltIns : IWorkflowDefinitionProvider
{
    public const string ApproveItems = "workflows.approveItems";
    public const string AiBatch = "workflows.aiBatch";

    public IEnumerable<BuiltInWorkflow> GetWorkflows() =>
    [
        new(ApproveItems, "Approve new items", "Asks approvers to approve each new item of a list and sets its status to the decision.",
            JsonNode.Parse("""
                {
                  "trigger": { "type": "itemAdded", "list": "{param:list}" },
                  "flow": {
                    "start": "approve",
                    "nodes": {
                      "approve": { "activity": "approval", "inputs": { "assignees": "{param:approvers}", "title": "Approve {title}", "dueInHours": "{param:dueInHours}" },
                                   "next": { "approved": "approved", "rejected": "rejected" } },
                      "approved": { "activity": "item.update", "inputs": { "fields": { "{param:statusField}": "{param:approvedValue}" } } },
                      "rejected": { "activity": "item.update", "inputs": { "fields": { "{param:statusField}": "{param:rejectedValue}" } } }
                    }
                  }
                }
                """)!.AsObject())
        {
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "list": { "type": "string", "description": "The list whose new items need approval." },
                    "approvers": { "type": "array", "description": "User names, group:Name or field:name." },
                    "statusField": { "type": "string", "default": "status", "description": "The field set to the decision." },
                    "approvedValue": { "type": "string", "default": "Approved" },
                    "rejectedValue": { "type": "string", "default": "Rejected" },
                    "dueInHours": { "type": "number", "description": "Escalates after this many hours (optional)." }
                  },
                  "required": ["list", "approvers"]
                }
                """)!.AsObject(),
        },
        new(AiBatch, "AI batch",
            "Answers the workspace's batched AI questions (steps with execution: batch) on a schedule, through the provider's batch API when the server has one.",
            JsonNode.Parse("""
                {
                  "trigger": { "type": "schedule", "cron": "{param:schedule}", "timeZone": "{param:timeZone}" },
                  "flow": {
                    "start": "batch",
                    "nodes": {
                      "batch": { "activity": "ai.batch", "inputs": { "maxQuestions": "{param:maxQuestions}", "pollMinutes": "{param:pollMinutes}" },
                                 "retry": { "attempts": 3, "delayMinutes": 10 } }
                    }
                  }
                }
                """)!.AsObject())
        {
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "schedule": { "type": "string", "default": "0 1 * * *", "description": "When to send (cron), e.g. 0 1,13 * * * for twice a day." },
                    "timeZone": { "type": "string", "description": "The schedule's time zone (default: the organization's)." },
                    "maxQuestions": { "type": "number", "description": "Most questions per batch run (default 2000)." },
                    "pollMinutes": { "type": "number", "default": 5, "description": "How often the provider's batches are checked." }
                  }
                }
                """)!.AsObject(),
            Requires = BuiltInRequirements.Ai,
        },
    ];
}
