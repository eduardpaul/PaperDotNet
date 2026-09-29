using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// Workspace section <c>Workflows</c> in <c>urn:paperdotnet:workflow:1</c>: the workspace's workflows with their
/// definitions as JSON (they refer to lists and users by name, so they are portable). Workflows are matched by
/// name; a changed definition becomes a new version (ADR-0036: workflows travel with lists and libraries).
/// </summary>
internal class WorkflowTemplateHandler(WorkflowsDbContext db, TriggerCatalog triggers, ActionCatalog actions) : ITemplateHandler
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
        foreach (var workflow in workflows)
        {
            var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == workflow.Id && v.Number == workflow.CurrentVersion, cancellationToken);
            section.Add(new XElement(ItemName, version.Definition)
                .With("Name", workflow.Name).With("Description", workflow.Description).With("Enabled", workflow.Enabled));
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
            var (spec, error) = DefinitionJson.TryParse<WorkflowSpec>(element.Value);
            var errors = error is not null ? [error] : Definitions.Validate(spec, triggers.Keys, actions);
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
                    WorkflowWriter.Create(db, workspaceId, name, description, enabled, spec!);
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
}

/// <summary>
/// Reads the section <c>Automations</c> in <c>urn:paperdotnet:automation:2</c> of templates made before the rename to
/// workflows (ADR-0036); exports always write <see cref="WorkflowTemplateHandler"/>'s section.
/// </summary>
internal sealed class LegacyAutomationTemplateHandler(WorkflowsDbContext db, TriggerCatalog triggers, ActionCatalog actions)
    : WorkflowTemplateHandler(db, triggers, actions)
{
    public static readonly XNamespace LegacyNs = "urn:paperdotnet:automation:2";

    public override XName Element => LegacyNs + "Automations";

    protected override XName ItemName => LegacyNs + "Automation";

    public override Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken) => Task.FromResult<XElement?>(null);
}
