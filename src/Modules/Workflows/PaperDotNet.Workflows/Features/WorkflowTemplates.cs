using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Workspace section <c>Workflows</c> in <c>urn:paperdotnet:workflow:1</c>: the workspace's workflows with their
/// definitions as JSON (they refer to lists and users by name, so they are portable). Workflows are matched by name; a
/// changed definition becomes a new version (ADR-0036: workflows travel with lists and libraries). Built-in workflows
/// (EVT-12) travel as their key (<c>BuiltIn</c>), on or off, with their parameters as JSON; copies keep <c>CopiedFrom</c>.
/// A built-in workflow this server does not have is skipped with a warning.
/// </summary>
internal sealed class WorkflowTemplateHandler(
    WorkflowsDbContext db, IEnumerable<IWorkflowActivity> activities, TriggerCatalog triggers, BuiltInWorkflows builtIns, TimeProvider time) : ITemplateHandler
{
    public static readonly XNamespace Ns = "urn:paperdotnet:workflow:1";

    public XName Element => Ns + "Workflows";

    public TemplateLevel Level => TemplateLevel.Workspace;

    public int Order => 500;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var workflows = await WorkflowsAsync(context, tracking: false, cancellationToken);
        if (workflows.Count == 0)
        {
            return null;
        }

        var section = new XElement(Element);
        foreach (var workflow in workflows.OrderBy(w => w.Name, StringComparer.Ordinal))
        {
            if (workflow.ListId is not null)
            {
                continue; // Per-library built-in workflows follow their library (documents section, T15).
            }

            if (workflow.BuiltInKey is { } builtIn)
            {
                section.Add(new XElement(Ns + "Workflow", workflow.Parameters ?? "{}").With("Name", workflow.Name).With("BuiltIn", builtIn).With("Enabled", workflow.Enabled));
                continue;
            }

            if (await WorkflowVersions.FindAsync(db, context.TenantId, workflow.Id, workflow.CurrentVersion, cancellationToken) is not { } version)
            {
                continue;
            }

            section.Add(new XElement(Ns + "Workflow", version.Definition)
                .With("Name", workflow.Name).With("Description", workflow.Description).With("Enabled", workflow.Enabled).With("CopiedFrom", workflow.CopiedFrom));
        }

        return section;
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var workspaceId = context.WorkspaceId!.Value;
        var prefix = context.WorkspaceName;
        var existing = context.IsPlanned ? [] : await WorkflowsAsync(context, tracking: true, cancellationToken);
        var catalog = activities.ToDictionary(a => a.Key, StringComparer.Ordinal);
        foreach (var element in section.Elements(Ns + "Workflow"))
        {
            var name = element.RequiredAttr("Name").Trim();
            if (element.Attr("BuiltIn") is { } key)
            {
                await ApplyBuiltInAsync(element, name, key, existing, context, cancellationToken);
                continue;
            }

            JsonObject? definition;
            try
            {
                definition = JsonNode.Parse(element.Value) as JsonObject;
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new TemplateException($"Workflow '{name}': the definition is not valid JSON ({ex.Message}).", element);
            }

            var (spec, error) = definition is null ? (null, "Not a workflow definition.") : WorkflowJson.Read(definition);
            var errors = spec is null ? [error ?? "Not a workflow definition."] : DefinitionValidator.Validate(spec, catalog, triggers);
            if (errors.Count > 0)
            {
                throw new TemplateException($"Workflow '{name}': {string.Join(" ", errors)}", element);
            }

            var json = WorkflowJson.Serialize(spec!);
            var description = element.Attr("Description");
            var enabled = element.BoolAttr("Enabled", true);
            var workflow = existing.FirstOrDefault(w => w.Name == name);
            if (workflow is null)
            {
                context.Created("workflow", $"{prefix}: {name}");
                if (!context.DryRun)
                {
                    workflow = new WorkflowDefinition
                    {
                        Id = Ids.New(),
                        TenantId = context.TenantId,
                        WorkspaceId = workspaceId,
                        Name = name,
                        Key = await WorkflowEndpoints.UniqueKeyAsync(db, context.TenantId, workspaceId, name, cancellationToken),
                        Description = description,
                        Enabled = enabled,
                        CopiedFrom = element.Attr("CopiedFrom"),
                        CurrentVersion = 1,
                        TriggerTypes = WorkflowEndpoints.TriggerTypes(spec!),
                    };
                    db.Workflows.Add(workflow);
                    db.WorkflowVersions.Add(new WorkflowVersion { Id = Ids.New(), TenantId = context.TenantId, WorkflowId = workflow.Id, Number = 1, Definition = json, CreatedAt = time.GetUtcNow() });
                }

                continue;
            }

            var current = await WorkflowVersions.FindAsync(db, context.TenantId, workflow.Id, workflow.CurrentVersion, cancellationToken);
            var changed = current?.Definition != json;
            if (changed || workflow.Description != description || workflow.Enabled != enabled)
            {
                if (changed)
                {
                    // A new version: running runs keep theirs.
                    workflow.CurrentVersion++;
                    workflow.TriggerTypes = WorkflowEndpoints.TriggerTypes(spec!);
                    if (!context.DryRun)
                    {
                        db.WorkflowVersions.Add(new WorkflowVersion { Id = Ids.New(), TenantId = context.TenantId, WorkflowId = workflow.Id, Number = workflow.CurrentVersion, Definition = json, CreatedAt = time.GetUtcNow() });
                    }
                }

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

    /// <summary>A built-in workflow: turned on or off with the template's parameters (a dry run only checks them).</summary>
    private async Task ApplyBuiltInAsync(XElement element, string name, string key, List<WorkflowDefinition> existing, TemplateContext context, CancellationToken ct)
    {
        if (await builtIns.FindAsync(context.TenantId, key, ct) is not { Scope: BuiltInScope.Workspace } workflow)
        {
            context.Warn($"Workflow '{name}': the built-in workflow '{key}' is not available on this server; skipped.", element);
            return;
        }

        JsonObject? parameters;
        try
        {
            parameters = string.IsNullOrWhiteSpace(element.Value) ? null : JsonNode.Parse(element.Value) as JsonObject;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new TemplateException($"Workflow '{name}': the parameters are not valid JSON ({ex.Message}).", element);
        }

        var enabled = element.BoolAttr("Enabled", true);
        var prefix = context.WorkspaceName;
        var row = existing.FirstOrDefault(w => w.BuiltInKey == key && w.ListId is null);
        var (_, values, _) = BuiltInWorkflows.Resolve(workflow, parameters);
        if (row is not null && row.Enabled == enabled && row.Parameters == values.ToJsonString())
        {
            return;
        }

        if (context.IsPlanned || context.DryRun)
        {
            // The lists it names may only be created by this template: checked when it is applied.
            if (row is null)
            {
                context.Created("workflow", $"{prefix}: {workflow.Name}", key);
            }
            else
            {
                context.Updated("workflow", $"{prefix}: {workflow.Name}", key);
            }

            return;
        }

        // After the lists: the lists it names may be created by this template.
        var workspaceId = context.WorkspaceId!.Value;
        context.Defer(async deferredCt =>
        {
            var (_, errors, _) = await builtIns.SetAsync(context.TenantId, workspaceId, workflow, enabled, parameters ?? [], deferredCt);
            if (errors.Count > 0)
            {
                throw new TemplateException($"Workflow '{name}' ({key}): {string.Join(" ", errors)}", element);
            }

            await db.SaveChangesAsync(deferredCt);
        });
        if (row is null)
        {
            context.Created("workflow", $"{prefix}: {workflow.Name}", key);
        }
        else
        {
            context.Updated("workflow", $"{prefix}: {workflow.Name}", key);
        }
    }

    private async Task<List<WorkflowDefinition>> WorkflowsAsync(TemplateContext context, bool tracking, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = context.TenantId;
        var workspace = context.WorkspaceId!.Value;
        var ct = cancellationToken;
        return tracking
            ? await database.Workflows.Where(w => w.TenantId == tenant && w.WorkspaceId == workspace).ToListAsync(ct)
            : await database.Workflows.AsNoTracking().Where(w => w.TenantId == tenant && w.WorkspaceId == workspace).ToListAsync(ct);
    }
}
