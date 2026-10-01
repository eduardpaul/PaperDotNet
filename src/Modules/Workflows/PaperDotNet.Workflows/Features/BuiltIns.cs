using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>Creates workflow rows and their versions (people's workflows, built-in ones and copies).</summary>
internal static class WorkflowWriter
{
    public static WorkflowDefinition Create(
        WorkflowsDbContext db, Guid tenantId, Guid workspaceId, string name, string key, string? description, bool enabled, WorkflowSpec spec, DateTimeOffset now, Guid? listId = null)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Ids.New(),
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            Name = name,
            Key = key,
            Description = description,
            Enabled = enabled,
            CurrentVersion = 1,
            TriggerTypes = WorkflowEndpoints.TriggerTypes(spec),
            ListId = listId,
        };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(new WorkflowVersion { Id = Ids.New(), TenantId = tenantId, WorkflowId = workflow.Id, Number = 1, Definition = WorkflowJson.Serialize(spec), CreatedAt = now });
        return workflow;
    }

    /// <summary>Makes <paramref name="spec"/> the workflow's current definition: a new version when it changed.</summary>
    public static async Task<bool> SetSpecAsync(WorkflowsDbContext db, WorkflowDefinition workflow, WorkflowSpec spec, DateTimeOffset now, CancellationToken ct)
    {
        var json = WorkflowJson.Serialize(spec);
        if ((await WorkflowVersions.FindAsync(db, workflow.TenantId, workflow.Id, workflow.CurrentVersion, ct))?.Definition == json)
        {
            return false;
        }

        workflow.CurrentVersion++;
        workflow.TriggerTypes = WorkflowEndpoints.TriggerTypes(spec);
        db.WorkflowVersions.Add(new WorkflowVersion { Id = Ids.New(), TenantId = workflow.TenantId, WorkflowId = workflow.Id, Number = workflow.CurrentVersion, Definition = json, CreatedAt = now });
        return true;
    }
}

/// <summary>
/// Built-in workflows (EVT-12, ADR-0036): the catalog of what modules and extensions ship
/// (<see cref="IWorkflowDefinitionProvider"/>), and their state in a workspace. A turned-on built-in workflow is a workflow
/// row with a <c>BuiltInKey</c>: its definition is the release's with the workspace's parameter values filled in, it cannot
/// be edited (copy it instead), and a new release's definition becomes a new version (<see cref="BuiltInSyncJob"/>).
/// </summary>
public sealed partial class BuiltInWorkflows(
    IEnumerable<IWorkflowDefinitionProvider> providers,
    IEnumerable<IWorkflowRequirement> requirements,
    IEnumerable<IWorkflowActivity> activities,
    TriggerCatalog triggers,
    TriggerTerms terms,
    WorkflowsDbContext db,
    WorkflowItems items,
    TimeProvider time)
{
    /// <summary>The built-in workflows offered in the tenant (an extension's only where it is enabled).</summary>
    public async Task<IReadOnlyList<BuiltInWorkflow>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        var offered = new List<BuiltInWorkflow>();
        foreach (var provider in providers)
        {
            if (await provider.IsAvailableAsync(tenantId, ct))
            {
                offered.AddRange(provider.GetWorkflows());
            }
        }

        return [.. offered.DistinctBy(w => w.Key).OrderBy(w => w.Name, StringComparer.Ordinal)];
    }

    public async Task<BuiltInWorkflow?> FindAsync(Guid tenantId, string key, CancellationToken ct) => (await ListAsync(tenantId, ct)).FirstOrDefault(w => w.Key == key);

    /// <summary>Whether the server has what the workflow needs.</summary>
    public bool IsAvailable(BuiltInWorkflow workflow) =>
        workflow.Requires is null || requirements.Any(r => r.Name == workflow.Requires && r.IsMet);

    /// <summary>
    /// The definition with the parameter values filled in (missing values take the schema's <c>default</c>): the definition,
    /// the values used, or why they do not fit. A per-library workflow gets the library's name as <c>{param:list}</c>.
    /// </summary>
    public static (WorkflowSpec? Spec, JsonObject Values, string? Error) Resolve(BuiltInWorkflow workflow, JsonObject? parameters, string? listName = null)
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

        var filled = values.DeepClone().AsObject();
        if (listName is not null)
        {
            filled["list"] = listName;
        }

        var (spec, invalid) = Fill(workflow.Definition, filled) is JsonObject definition ? WorkflowJson.Read(definition) : (null, "Not an object.");
        return (spec, values, spec is null ? $"The definition is not valid: {invalid}" : null);
    }

    /// <summary>The definition for the workspace, or why the workflow cannot run there with these values.</summary>
    public async Task<(WorkflowSpec? Spec, JsonObject Values, List<string> Errors)> CheckAsync(
        Guid tenantId, Guid workspaceId, BuiltInWorkflow workflow, JsonObject? parameters, CancellationToken ct, string? listName = null)
    {
        if (!IsAvailable(workflow))
        {
            return (null, parameters ?? [], [$"'{workflow.Name}' needs {workflow.Requires} to be configured on the server."]);
        }

        var (spec, values, error) = Resolve(workflow, parameters, listName);
        if (error is not null)
        {
            return (null, values, [error]);
        }

        var errors = DefinitionValidator.Validate(spec!, activities.ToDictionary(a => a.Key, StringComparer.Ordinal), triggers);
        if (errors.Count == 0)
        {
            errors.AddRange(await terms.CheckAsync(tenantId, spec!, ct));
        }

        // The lists its triggers name must exist in the workspace.
        var reader = new ChangeActor(tenantId, null);
        foreach (var list in spec!.AllTriggers.Select(t => t.List).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (await items.FindListByNameAsync(reader, workspaceId, list, ct) is null)
            {
                errors.Add($"The list '{list}' does not exist in the workspace.");
            }
        }

        return (errors.Count == 0 ? spec : null, values, errors);
    }

    /// <summary>The built-in workflow's row in the workspace, or in the library for per-library ones (null when it was never turned on).</summary>
    public Task<WorkflowDefinition?> RowAsync(Guid tenantId, Guid workspaceId, string key, CancellationToken cancellationToken, Guid? listId = null)
    {
        var context = db;
        var tenant = tenantId;
        var workspace = workspaceId;
        var builtIn = key;
        var list = listId;
        var ct = cancellationToken;
        return context.Workflows.FirstOrDefaultAsync(w => w.TenantId == tenant && w.WorkspaceId == workspace && w.BuiltInKey == builtIn && w.ListId == list, ct);
    }

    /// <summary>The name of a built-in workflow's row: per library, with the library's name (names are unique in a workspace).</summary>
    public static string RowName(BuiltInWorkflow workflow, ListData? list) => list is null ? workflow.Name : $"{workflow.Name} ({list.Name})";

    public static JsonObject? Values(WorkflowDefinition? row) => row?.Parameters is { } json ? JsonNode.Parse(json) as JsonObject : null;

    /// <summary>
    /// Turns the built-in workflow on (with <paramref name="parameters"/>, else the ones it had) or off in the workspace. The
    /// row is added to the context, not saved. Errors when the values do not fit, or a workflow of the same name exists.
    /// </summary>
    public async Task<(WorkflowDefinition? Row, List<string> Errors, bool NameTaken)> SetAsync(
        Guid tenantId, Guid workspaceId, BuiltInWorkflow workflow, bool enabled, JsonObject? parameters, CancellationToken ct, ListData? list = null)
    {
        if ((workflow.Scope == BuiltInScope.Library) != (list is not null))
        {
            return (null, [workflow.Scope == BuiltInScope.Library
                ? $"'{workflow.Name}' is turned on per library (…/lists/{{listId}}/workflows/builtIns)."
                : $"'{workflow.Name}' is turned on in the workspace, not per list."], false);
        }

        if (list is { IsLibrary: false })
        {
            return (null, [$"'{workflow.Name}' works on document libraries; '{list.Name}' is a list."], false);
        }

        var row = await RowAsync(tenantId, workspaceId, workflow.Key, ct, list?.Id);
        if (!enabled && parameters is null)
        {
            if (row is not null)
            {
                row.Enabled = false;
                return (row, [], false);
            }

            if (!workflow.EnabledByDefault)
            {
                return (null, [], false);
            }

            // Off where it would be on by default: the row is kept, off, so it is not turned on again.
        }

        var (spec, values, errors) = await CheckAsync(tenantId, workspaceId, workflow, parameters ?? Values(row), ct, list?.Name);
        if (errors.Count > 0)
        {
            return (null, errors, false);
        }

        var name = RowName(workflow, list);
        var now = time.GetUtcNow();
        if (row is null)
        {
            if (await WorkflowEndpoints.NameTakenAsync(db, tenantId, workspaceId, name, Guid.Empty, ct))
            {
                return (null, [$"A workflow named '{name}' already exists in the workspace."], true);
            }

            row = WorkflowWriter.Create(db, tenantId, workspaceId, name, workflow.Key, workflow.Description, enabled, spec!, now, list?.Id);
            row.BuiltInKey = workflow.Key;
        }
        else
        {
            await WorkflowWriter.SetSpecAsync(db, row, spec!, now, ct);
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
        Guid tenantId, Guid workspaceId, BuiltInWorkflow workflow, string name, JsonObject? parameters, CancellationToken ct)
    {
        var row = await RowAsync(tenantId, workspaceId, workflow.Key, ct);
        var (spec, _, errors) = await CheckAsync(tenantId, workspaceId, workflow, parameters ?? Values(row), ct);
        if (errors.Count > 0)
        {
            return (null, errors, false);
        }

        if (await WorkflowEndpoints.NameTakenAsync(db, tenantId, workspaceId, name, Guid.Empty, ct))
        {
            return (null, [$"A workflow named '{name}' already exists in the workspace."], true);
        }

        var key = await WorkflowEndpoints.UniqueKeyAsync(db, tenantId, workspaceId, name, ct);
        var copy = WorkflowWriter.Create(db, tenantId, workspaceId, name, key, workflow.Description, true, spec!, time.GetUtcNow());
        copy.CopiedFrom = workflow.Key;
        if (row is not null)
        {
            row.Enabled = false;
        }

        return (copy, [], false);
    }

    /// <summary>
    /// Creates the per-library built-in workflows that are on by default in <paramref name="list"/> (a library), unless the
    /// library has them already (on or off). Checked once per library and process.
    /// </summary>
    public async Task EnsureDefaultsAsync(Guid tenantId, ListData list, CancellationToken ct)
    {
        // Marked only once the rows are saved: an event handled at the same moment must not match before they exist.
        if (!list.IsLibrary || EnsuredLibraries.ContainsKey((tenantId, list.Id)))
        {
            return;
        }

        // One at a time in this process (the catalog and a trigger often ask at once); other servers are caught below.
        await EnsureLock.WaitAsync(ct);
        try
        {
            if (!EnsuredLibraries.ContainsKey((tenantId, list.Id)))
            {
                await CreateDefaultsAsync(tenantId, list, ct);
            }
        }
        finally
        {
            EnsureLock.Release();
        }
    }

    private static readonly SemaphoreSlim EnsureLock = new(1, 1);

    private async Task CreateDefaultsAsync(Guid tenantId, ListData list, CancellationToken ct)
    {
        foreach (var workflow in (await ListAsync(tenantId, ct)).Where(w => w.Scope == BuiltInScope.Library && w.EnabledByDefault && IsAvailable(w)))
        {
            if (await RowAsync(tenantId, list.WorkspaceId, workflow.Key, ct, list.Id) is not null)
            {
                continue;
            }

            var (row, errors, _) = await SetAsync(tenantId, list.WorkspaceId, workflow, true, null, ct, list);
            if (row is null || errors.Count > 0)
            {
                continue;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Created at the same moment by another request: that one counts.
                db.ChangeTracker.Clear();
            }
        }

        EnsuredLibraries.TryAdd((tenantId, list.Id), true);
    }

    /// <summary>Libraries whose default workflows exist (per tenant), so they are checked once per process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Tenant, Guid List), bool> EnsuredLibraries = new();

    /// <summary>
    /// Brings the tenant's built-in workflows up to the release: a changed definition becomes a new version; one the release
    /// no longer has (or whose extension was turned off) is turned off; one that no longer fits its workspace keeps its version.
    /// </summary>
    public async Task SyncAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var offered = (await ListAsync(tenantId, cancellationToken)).ToDictionary(w => w.Key);
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        var rows = await context.Workflows.Where(w => w.TenantId == tenant && w.BuiltInKey != null).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (!offered.TryGetValue(row.BuiltInKey!, out var workflow))
            {
                row.Enabled = false;
                continue;
            }

            // A per-library workflow follows its library's name; a removed library turns it off.
            ListData? list = null;
            if (row.ListId is { } listId)
            {
                list = await items.FindListAsync(new ChangeActor(tenantId, null), row.WorkspaceId, listId, ct);
                if (list is null)
                {
                    row.Enabled = false;
                    continue;
                }
            }

            var (spec, _, errors) = await CheckAsync(tenantId, row.WorkspaceId, workflow, Values(row), ct, list?.Name);
            if (errors.Count == 0)
            {
                await WorkflowWriter.SetSpecAsync(db, row, spec!, time.GetUtcNow(), ct);
                row.Description = workflow.Description;
                var name = RowName(workflow, list);
                if (row.Name != name && !await WorkflowEndpoints.NameTakenAsync(db, tenantId, row.WorkspaceId, name, row.Id, ct))
                {
                    row.Name = name;
                }
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
        null => "",
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

    public Task RunAsync(Guid tenantId, CancellationToken cancellationToken) => builtIns.SyncAsync(tenantId, cancellationToken);
}

/// <summary>The workflow module's own built-in workflows: templates people turn on for a list.</summary>
internal static class WorkflowBuiltIns
{
    public const string ApproveItems = "workflows.approveItems";

    public static readonly BuiltInWorkflow ApproveItemsWorkflow =
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
        };
}
