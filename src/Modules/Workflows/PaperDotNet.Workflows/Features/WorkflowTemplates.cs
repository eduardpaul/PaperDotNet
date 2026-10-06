using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Workspace section <c>Workflows</c> in <c>urn:paperdotnet:workflow:1</c>: the workspace's workflows with their
/// definitions as JSON (they refer to lists and users by name, so they are portable). Workflows are matched by
/// name; a changed definition becomes a new version (ADR-0036: workflows travel with lists and libraries). A built-in
/// workflow (EVT-12) is written as its key (<c>BuiltIn</c>) with its parameter values instead of a definition, so the
/// target gets its own release's definition; a copy of one keeps where it came from (<c>CopiedFrom</c>). A per-library
/// built-in workflow names its library (<c>List</c>, ADR-0038); a workflow's <c>Key</c> (the name of its events) travels too.
/// </summary>
internal class WorkflowTemplateHandler(
    WorkflowsDbContext db, TriggerCatalog triggers, ActionCatalog actions, BuiltInWorkflows builtIns, IListItemStore items) : ITemplateHandler
{
    public static readonly XNamespace Ns = "urn:paperdotnet:workflow:1";

    public virtual XName Element => Ns + "Workflows";

    /// <summary>The element of one workflow in the section.</summary>
    protected virtual XName ItemName => Ns + "Workflow";

    public TemplateLevel Level => TemplateLevel.Workspace;

    public int Order => 500;

    public virtual async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var workflows = await db.Workflows.AsNoTracking().Where(a => a.WorkspaceId == context.WorkspaceId).OrderBy(a => a.Name).ToListAsync(cancellationToken);
        if (workflows.Count == 0)
        {
            return null;
        }

        var section = new XElement(Element);
        var lists = (await items.AsSystem().GetListsAsync(context.WorkspaceId!.Value, null, cancellationToken)).ToDictionary(l => l.Id, l => l.Name);
        foreach (var workflow in workflows)
        {
            if (workflow.BuiltInKey is { } key)
            {
                if (workflow.ListId is { } listId && !lists.ContainsKey(listId))
                {
                    continue; // Its library is gone.
                }

                section.Add(new XElement(ItemName, workflow.Parameters ?? "{}").With("Name", workflow.Name).With("BuiltIn", key)
                    .With("List", workflow.ListId is { } id ? lists[id] : null).With("Enabled", workflow.Enabled));
                continue;
            }

            var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == workflow.Id && v.Number == workflow.CurrentVersion, cancellationToken);
            section.Add(new XElement(ItemName, version.Definition)
                .With("Name", workflow.Name).With("Key", workflow.EventKey).With("Description", workflow.Description).With("Enabled", workflow.Enabled)
                .With("CopiedFrom", workflow.CopiedFrom));
        }

        return section;
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var workspaceId = context.WorkspaceId!.Value;
        var prefix = context.WorkspaceName;
        foreach (var element in section.Elements(ItemName))
        {
            var name = element.RequiredAttr("Name").Trim();
            if (element.Attr("BuiltIn") is { } key)
            {
                var (workspace, workspaceName, planned) = (workspaceId, context.WorkspaceName, context.IsPlanned);
                if (element.Attr("List") is null)
                {
                    await ApplyBuiltInAsync(element, key, name, context, workspace, workspaceName, planned, cancellationToken);
                    continue;
                }

                // A per-library workflow once the template's lists exist (they are applied after the workspace's sections).
                context.Defer(async ct =>
                {
                    await ApplyBuiltInAsync(element, key, name, context, workspace, workspaceName, planned, ct);
                    if (context.DryRun)
                    {
                        return;
                    }

                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException)
                    {
                        // The library's defaults were created meanwhile (its imported items raised events): set that row.
                        db.ChangeTracker.Clear();
                        await ApplyBuiltInAsync(element, key, name, context, workspace, workspaceName, planned, ct);
                        await db.SaveChangesAsync(ct);
                    }
                });
                continue;
            }

            var (spec, error) = DefinitionJson.TryParse<WorkflowSpec>(element.Value);
            var errors = error is not null ? [error] : Definitions.Validate(spec, triggers.Keys, actions);
            if (element.Attr("Key") is { } chosen && !WorkflowKeys.IsValid(chosen))
            {
                errors.Add($"the key '{chosen}' is not valid (lower case letters, digits, dashes and underscores).");
            }

            if (errors.Count > 0)
            {
                throw new TemplateException($"Workflow '{name}': {string.Join(" ", errors)}", element);
            }

            var description = element.Attr("Description");
            var enabled = element.BoolAttr("Enabled", true);
            var workflow = context.IsPlanned ? null : await db.Workflows.FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.Name == name, cancellationToken);
            if (workflow is null)
            {
                context.Created("workflow", $"{prefix}: {name}");
                if (!context.DryRun)
                {
                    var eventKey = element.Attr("Key") is { } wanted && !await WorkflowWriter.KeyTakenAsync(db, workspaceId, wanted, null, cancellationToken)
                        ? wanted
                        : await WorkflowWriter.KeyForAsync(db, workspaceId, name, cancellationToken);
                    WorkflowWriter.Create(db, workspaceId, name, description, enabled, spec!, eventKey).CopiedFrom = element.Attr("CopiedFrom");
                }

                continue;
            }

            var changed = await WorkflowWriter.SetSpecAsync(db, workflow, spec!, cancellationToken);
            if (changed || workflow.Description != description || workflow.Enabled != enabled)
            {
                context.Updated("workflow", $"{prefix}: {name}", changed ? $"version {workflow.CurrentVersion}" : null);
                workflow.Description = description;
                workflow.Enabled = enabled;
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>A built-in workflow's state: this release's definition with the template's parameter values.</summary>
    private async Task ApplyBuiltInAsync(
        XElement element, string key, string name, TemplateContext context, Guid workspaceId, string? workspaceName, bool isPlanned, CancellationToken ct)
    {
        var enabled = element.BoolAttr("Enabled", true);
        if (await builtIns.FindAsync(key, ct) is not { } builtIn)
        {
            if (!enabled)
            {
                // Turned off where it was exported: nothing to set up here (e.g. its extension is not enabled).
                context.Warn($"Workflow '{name}': the built-in workflow '{key}' does not exist on this server; skipped (it was turned off).", element);
                return;
            }

            throw new TemplateException($"Workflow '{name}': the built-in workflow '{key}' does not exist on this server.", element);
        }

        // A per-library workflow belongs to the library the template names (created earlier in the same template, or existing).
        var listName = element.Attr("List");
        if ((builtIn.Scope != BuiltInScope.Workspace) != (listName is not null))
        {
            throw new TemplateException(builtIn.Scope == BuiltInScope.Library
                ? $"Workflow '{name}': the built-in workflow '{key}' is turned on per library; name it with List."
                : $"Workflow '{name}': the built-in workflow '{key}' is turned on in the workspace, not per list.", element);
        }

        ListData? list = null;
        if (listName is not null && !isPlanned)
        {
            list = (await items.AsSystem().GetListsAsync(workspaceId, null, ct)).FirstOrDefault(l => l.Name == listName);
            if (list is null && !context.DryRun)
            {
                throw new TemplateException($"Workflow '{name}': the list '{listName}' does not exist in the workspace.", element);
            }
        }

        var values = JsonNode.Parse(string.IsNullOrWhiteSpace(element.Value) ? "{}" : element.Value) as JsonObject;
        var (spec, _, error) = BuiltInWorkflows.Resolve(builtIn, values, listName);
        var errors = error is not null ? [error] : Definitions.Validate(spec, triggers.Keys, actions);
        if (errors.Count > 0)
        {
            throw new TemplateException($"Workflow '{name}': {string.Join(" ", errors)}", element);
        }

        if (enabled && !builtIns.IsAvailable(builtIn))
        {
            // The rest of the template still applies; the workflow is set up turned off until the server has what it needs.
            context.Warn($"Workflow '{name}': it needs {builtIn.Requires} to be configured on the server, so it is turned off.", element);
            enabled = false;
        }

        var rowName = listName is null ? builtIn.Name : $"{builtIn.Name} ({listName})";
        var row = isPlanned || (listName is not null && list is null) ? null : await builtIns.RowAsync(workspaceId, key, ct, list?.Id);
        if (row is null && !isPlanned)
        {
            // An imported-item event can create the default after RowAsync: reuse it only if its identity matches.
            row = await db.Workflows.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.Name == rowName, ct);
            if (row is not null && (row.BuiltInKey != key || row.ListId != list?.Id))
            {
                throw new TemplateException($"Workflow '{name}': a workflow named '{rowName}' already exists in the workspace.", element);
            }
        }

        if (row is null)
        {
            context.Created("workflow", $"{workspaceName}: {rowName}");
            if (!context.DryRun)
            {
                var created = WorkflowWriter.Create(db, workspaceId, rowName, builtIn.Description, enabled, spec!, key, list?.Id);
                created.BuiltInKey = key;
                created.Parameters = values?.ToJsonString() ?? "{}";
            }

            return;
        }

        var changed = await WorkflowWriter.SetSpecAsync(db, row, spec!, ct);
        var parameters = values?.ToJsonString() ?? "{}";
        if (changed || row.Enabled != enabled || row.Parameters != parameters)
        {
            context.Updated("workflow", $"{workspaceName}: {row.Name}", changed ? $"version {row.CurrentVersion}" : null);
            row.Enabled = enabled;
            row.Parameters = parameters;
        }
    }
}

/// <summary>
/// Reads the section <c>Automations</c> in <c>urn:paperdotnet:automation:2</c> of templates made before the rename to
/// workflows (ADR-0036); exports always write <see cref="WorkflowTemplateHandler"/>'s section.
/// </summary>
internal sealed class LegacyAutomationTemplateHandler(
    WorkflowsDbContext db, TriggerCatalog triggers, ActionCatalog actions, BuiltInWorkflows builtIns, IListItemStore items)
    : WorkflowTemplateHandler(db, triggers, actions, builtIns, items)
{
    public static readonly XNamespace LegacyNs = "urn:paperdotnet:automation:2";

    public override XName Element => LegacyNs + "Automations";

    protected override XName ItemName => LegacyNs + "Automation";

    public override Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken) => Task.FromResult<XElement?>(null);
}
