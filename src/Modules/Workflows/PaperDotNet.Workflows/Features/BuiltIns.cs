using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
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
    IEnumerable<IWorkflowDefinitionProvider> providers, IServiceProvider services, WorkflowsDbContext db, WorkflowValidator validator, IListItemStore items)
{
    /// <summary>The built-in workflows offered in the organization (an extension's only where it is enabled).</summary>
    public async Task<IReadOnlyList<BuiltInWorkflow>> ListAsync(CancellationToken ct)
    {
        var offered = new List<BuiltInWorkflow>();
        foreach (var provider in providers)
        {
            if (await provider.IsAvailableAsync(ct))
            {
                offered.AddRange(provider.GetWorkflows());
            }
        }

        return [.. offered.DistinctBy(w => w.Key).OrderBy(w => w.Name, StringComparer.Ordinal)];
    }

    public async Task<BuiltInWorkflow?> FindAsync(string key, CancellationToken ct) => (await ListAsync(ct)).FirstOrDefault(w => w.Key == key);

    /// <summary>The process roles offered, each with its default built-in (whose key is the role; its flags are the role's).</summary>
    public async Task<IReadOnlyDictionary<string, BuiltInWorkflow>> RolesAsync(CancellationToken ct) =>
        (await ListAsync(ct)).Where(w => w.RoleKey is not null && w.RoleKey == w.Key).ToDictionary(w => w.Key, StringComparer.Ordinal);

    /// <summary>Whether another enabled workflow (not <paramref name="except"/>) fills the role in the scope (a list, or the workspace).</summary>
    public Task<bool> HasOtherProviderAsync(Guid workspaceId, Guid? listId, string role, Guid? except, CancellationToken ct) =>
        db.Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.ListId == listId && w.Role == role && w.Enabled && w.Id != except, ct);

    /// <summary>
    /// One active workflow per role: turns off the other workflows of the row's role in its scope when the row is on
    /// (added to the context, not saved).
    /// </summary>
    public async Task ActivateAsync(WorkflowDefinition row, CancellationToken ct)
    {
        if (!row.Enabled || row.Role is not { } role)
        {
            return;
        }

        foreach (var other in await db.Workflows.Where(w => w.WorkspaceId == row.WorkspaceId && w.ListId == row.ListId && w.Role == role && w.Id != row.Id && w.Enabled).ToListAsync(ct))
        {
            other.Enabled = false;
        }
    }

    /// <summary>A required role left without an active workflow (its replacement turned off or deleted) gets its default back (saved).</summary>
    public async Task RestoreAsync(Guid workspaceId, Guid? listId, string? role, CancellationToken ct)
    {
        if (role is null || !(await RolesAsync(ct)).TryGetValue(role, out var fallback) || !fallback.Required || !IsAvailable(fallback)
            || await HasOtherProviderAsync(workspaceId, listId, role, null, ct))
        {
            return;
        }

        var list = listId is { } id ? await items.AsSystem().GetListAsync(workspaceId, id, ct) : null;
        if (listId is not null && list is null)
        {
            return;
        }

        var (row, errors, _) = await SetAsync(workspaceId, fallback, true, null, ct, list);
        if (row is not null && errors.Count == 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Whether the server has what the workflow needs.</summary>
    public bool IsAvailable(BuiltInWorkflow workflow) => workflow.Requires switch
    {
        null => true,
        BuiltInRequirements.Ai => services.GetService<IChatClient>() is not null,
        _ => false,
    };

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

        var (spec, invalid) = DefinitionJson.TryParse<WorkflowSpec>(Fill(workflow.Definition, filled)!.ToJsonString());
        // A system built-in fills its role; a copy keeps it, so it stays a system workflow and replaces the built-in.
        return (spec is null ? null : spec with { Provides = workflow.RoleKey }, values, invalid is null ? null : $"The definition is not valid: {invalid}");
    }

    /// <summary>Why the workflow cannot run in the workspace with these values, or null.</summary>
    public async Task<(WorkflowSpec? Spec, JsonObject Values, List<string> Errors)> CheckAsync(
        Guid workspaceId, BuiltInWorkflow workflow, JsonObject? parameters, CancellationToken ct, string? listName = null)
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

        var errors = await validator.ValidateAsync(workspaceId, spec!, ct);
        return (errors.Count == 0 ? spec : null, values, errors);
    }

    /// <summary>The built-in workflow's row in the workspace, or in the library for per-library ones (null when it was never enabled).</summary>
    public Task<WorkflowDefinition?> RowAsync(Guid workspaceId, string key, CancellationToken ct, Guid? listId = null) =>
        db.Workflows.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.BuiltInKey == key && w.ListId == listId, ct);

    /// <summary>The name of a built-in workflow's row: per library, with the library's name (names are unique in a workspace).</summary>
    public static string RowName(BuiltInWorkflow workflow, ListData? list) => list is null ? workflow.Name : $"{workflow.Name} ({list.Name})";

    public static JsonObject? Values(WorkflowDefinition? row) => row?.Parameters is { } json ? JsonNode.Parse(json) as JsonObject : null;

    /// <summary>
    /// Turns the built-in workflow on (with <paramref name="parameters"/>, else the ones it had) or off in the workspace.
    /// The row is added to the context, not saved. Errors when the values do not fit, or a workflow of the same name exists.
    /// </summary>
    public async Task<(WorkflowDefinition? Row, List<string> Errors, bool NameTaken)> SetAsync(
        Guid workspaceId, BuiltInWorkflow workflow, bool enabled, JsonObject? parameters, CancellationToken ct, ListData? list = null)
    {
        if ((workflow.Scope != BuiltInScope.Workspace) != (list is not null))
        {
            return (null, [workflow.Scope != BuiltInScope.Workspace
                ? $"'{workflow.Name}' is turned on per list (…/lists/{{listId}}/workflows/builtIns)."
                : $"'{workflow.Name}' is turned on in the workspace, not per list."], false);
        }

        if (workflow.Scope == BuiltInScope.Library && list is { IsLibrary: false })
        {
            return (null, [$"'{workflow.Name}' works on document libraries; '{list.Name}' is a list."], false);
        }

        if (!enabled && workflow.Locked)
        {
            return (null, [$"'{workflow.Name}' is a guarantee of the product and cannot be turned off."], false);
        }

        var row = await RowAsync(workspaceId, workflow.Key, ct, list?.Id);
        if (!enabled && workflow.Required && !await HasOtherProviderAsync(workspaceId, list?.Id, workflow.RoleKey!, row?.Id, ct))
        {
            return (null, [$"'{workflow.Name}' is required: replace it with a copy or another workflow for '{workflow.RoleKey}' instead of turning it off."], false);
        }

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

        var (spec, values, errors) = await CheckAsync(workspaceId, workflow, parameters ?? Values(row), ct, list?.Name);
        if (errors.Count > 0)
        {
            return (null, errors, false);
        }

        var name = RowName(workflow, list);
        if (row is null)
        {
            if (await db.Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Name == name, ct))
            {
                return (null, [$"A workflow named '{name}' already exists in the workspace."], true);
            }

            row = WorkflowWriter.Create(db, workspaceId, name, workflow.Description, enabled, spec!, workflow.Key, list?.Id);
            row.BuiltInKey = workflow.Key;
        }
        else
        {
            await WorkflowWriter.SetSpecAsync(db, row, spec!, ct);
            row.Enabled = enabled;
            row.Description = workflow.Description;
        }

        row.Parameters = values.ToJsonString();
        await ActivateAsync(row, ct);
        return (row, [], false);
    }

    /// <summary>Resolves a manual library workflow, creating its row with automatic runs off if it has none.</summary>
    public async Task<(WorkflowDefinition? Row, List<string> Errors, bool NameTaken)> PrepareManualAsync(
        ListData list, BuiltInWorkflow workflow, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var existing = await RowAsync(list.WorkspaceId, workflow.Key, ct, list.Id);
            var result = await SetAsync(list.WorkspaceId, workflow, existing?.Enabled ?? false, Values(existing) ?? new JsonObject(), ct, list);
            if (result.Row is null || result.Errors.Count > 0)
            {
                return result;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return result;
            }
            catch (DbUpdateException) when (existing is null && attempt < 2)
            {
                // Another launch (or enabling automatic runs) may have created the same library row.
                db.ChangeTracker.Clear();
                if (await RowAsync(list.WorkspaceId, workflow.Key, ct, list.Id) is null)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Creates the per-library built-in workflows that are on by default in <paramref name="list"/> (a library), unless the
    /// library has them already (on or off). Done once per library and process: when a document is added, or its
    /// settings are read.
    /// </summary>
    public async Task EnsureDefaultsAsync(ListData list, Guid tenantId, CancellationToken ct)
    {
        // Marked only once the rows are saved: an event handled at the same moment must not match before they exist.
        if (EnsuredLibraries.ContainsKey((tenantId, list.Id)))
        {
            return;
        }

        // The first events of a new workspace arrive together: they wait for each other here instead of all creating the
        // same rows and failing on the unique names (across servers, the unique indexes still decide).
        var gate = EnsureGates.GetOrAdd((tenantId, list.WorkspaceId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (EnsuredLibraries.ContainsKey((tenantId, list.Id)))
            {
                return;
            }

            var existing = (await db.Workflows.AsNoTracking().Where(w => w.WorkspaceId == list.WorkspaceId && w.BuiltInKey != null && (w.ListId == null || w.ListId == list.Id))
                .Select(w => new { w.BuiltInKey, w.ListId }).ToListAsync(ct)).Select(w => (w.BuiltInKey, w.ListId)).ToHashSet();
            foreach (var workflow in (await ListAsync(ct)).Where(w => (w.Scope == BuiltInScope.List || (w.Scope == BuiltInScope.Library && list.IsLibrary) || w.Scope == BuiltInScope.Workspace) && w.EnabledByDefault && IsAvailable(w)))
            {
                var scope = workflow.Scope == BuiltInScope.Workspace ? null : list;
                if (existing.Contains((workflow.Key, scope?.Id)))
                {
                    continue;
                }

                // A replacement already fills the role (e.g. made before the default existed): the default is created off.
                var replaced = workflow.RoleKey is { } role && await HasOtherProviderAsync(list.WorkspaceId, scope?.Id, role, null, ct);
                var (row, errors, _) = await SetAsync(list.WorkspaceId, workflow, !replaced, null, ct, scope);
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
                    // Created at the same moment by another server: that one counts.
                    db.ChangeTracker.Clear();
                }
            }

            EnsuredLibraries.TryAdd((tenantId, list.Id), true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One provisioning at a time per workspace and process (see <see cref="EnsureDefaultsAsync"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Tenant, Guid Workspace), SemaphoreSlim> EnsureGates = new();

    /// <summary>Libraries whose default workflows exist (per tenant), so they are checked once per process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Tenant, Guid List), bool> EnsuredLibraries = new();

    /// <summary>Forgets that a library's defaults were created (e.g. its rows were removed), so they are checked again.</summary>
    public static void ForgetLibrary(Guid tenantId, Guid listId) => EnsuredLibraries.TryRemove((tenantId, listId), out _);

    /// <summary>
    /// A workflow of the workspace's own made from the built-in one (to change it), named <paramref name="name"/>; the
    /// built-in one is turned off there. Added to the context, not saved.
    /// </summary>
    public async Task<(WorkflowDefinition? Copy, List<string> Errors, bool NameTaken)> CopyAsync(
        Guid workspaceId, BuiltInWorkflow workflow, string name, JsonObject? parameters, CancellationToken ct, ListData? list = null)
    {
        if (workflow.Locked || (workflow.RoleKey is { } role && (await RolesAsync(ct)).GetValueOrDefault(role)?.Locked == true))
        {
            return (null, [$"'{workflow.Name}' is a guarantee of the product; it cannot be replaced."], false);
        }

        if ((workflow.Scope != BuiltInScope.Workspace) != (list is not null))
        {
            return (null, [workflow.Scope != BuiltInScope.Workspace
                ? $"'{workflow.Name}' is copied per list (…/lists/{{listId}}/workflows/builtIns/{workflow.Key}/copy)."
                : $"'{workflow.Name}' is copied in the workspace, not per list."], false);
        }

        var row = await RowAsync(workspaceId, workflow.Key, ct, list?.Id);
        var (spec, _, errors) = await CheckAsync(workspaceId, workflow, parameters ?? Values(row), ct, list?.Name);
        if (errors.Count > 0)
        {
            return (null, errors, false);
        }

        if (await db.Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Name == name, ct))
        {
            return (null, [$"A workflow named '{name}' already exists in the workspace."], true);
        }

        // The copy fills the built-in's role (spec.Provides) in the same scope and becomes its active workflow.
        var copy = WorkflowWriter.Create(db, workspaceId, name, workflow.Description, true, spec!, await WorkflowWriter.KeyForAsync(db, workspaceId, name, ct), list?.Id);
        copy.CopiedFrom = workflow.Key;
        if (row is not null)
        {
            row.Enabled = false;
        }

        await ActivateAsync(copy, ct);
        return (copy, [], false);
    }

    /// <summary>
    /// Brings the organization's built-in workflows up to the release: a changed definition becomes a new version; one the
    /// release no longer has (or whose extension was turned off) is turned off; one that no longer fits its workspace
    /// (e.g. a list was renamed) keeps its version.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        var offered = (await ListAsync(ct)).ToDictionary(w => w.Key);
        foreach (var row in await db.Workflows.Where(w => w.BuiltInKey != null).ToListAsync(ct))
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
                list = await items.AsSystem().GetListAsync(row.WorkspaceId, listId, ct);
                if (list is null)
                {
                    row.Enabled = false;
                    continue;
                }
            }

            row.Role = workflow.RoleKey;
            var (spec, _, errors) = await CheckAsync(row.WorkspaceId, workflow, Values(row), ct, list?.Name);
            if (errors.Count == 0)
            {
                await WorkflowWriter.SetSpecAsync(db, row, spec!, ct);
                row.Description = workflow.Description;
                var name = RowName(workflow, list);
                if (row.Name != name && !await db.Workflows.AnyAsync(w => w.WorkspaceId == row.WorkspaceId && w.Name == name, ct))
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
