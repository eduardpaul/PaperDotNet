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
/// (EVT-12) come with T14.
/// </summary>
internal sealed class WorkflowTemplateHandler(WorkflowsDbContext db, IEnumerable<IWorkflowActivity> activities, TriggerCatalog triggers, TimeProvider time) : ITemplateHandler
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
            if (await WorkflowVersions.FindAsync(db, context.TenantId, workflow.Id, workflow.CurrentVersion, cancellationToken) is not { } version)
            {
                continue;
            }

            section.Add(new XElement(Ns + "Workflow", version.Definition)
                .With("Name", workflow.Name).With("Description", workflow.Description).With("Enabled", workflow.Enabled));
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
                context.Warn($"Workflow '{name}': built-in workflows ('{key}') are not available on this server yet; skipped.", element);
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
