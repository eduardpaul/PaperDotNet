using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Data;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Automation.Features;

/// <summary>
/// Workspace section <c>Automation</c> in <c>urn:paperdotnet:automation:1</c>: the workspace's rules and workflows
/// with their definitions as JSON (they refer to lists, users and workflows by name, so they are portable).
/// Rules and workflows are matched by name; changed workflow steps become a new version.
/// </summary>
internal sealed class AutomationTemplateHandler(AutomationDbContext db, TriggerCatalog triggers, ActionCatalog actions) : ITemplateHandler
{
    public static readonly XNamespace Ns = "urn:paperdotnet:automation:1";

    public XName Element => Ns + "Automation";

    public TemplateLevel Level => TemplateLevel.Workspace;

    public int Order => 500;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var rules = await db.Rules.AsNoTracking().Where(r => r.WorkspaceId == context.WorkspaceId).OrderBy(r => r.Name).ToListAsync(cancellationToken);
        var workflows = await db.Workflows.AsNoTracking().Where(w => w.WorkspaceId == context.WorkspaceId).OrderBy(w => w.Name).ToListAsync(cancellationToken);
        if (rules.Count == 0 && workflows.Count == 0)
        {
            return null;
        }

        var section = new XElement(Element);
        foreach (var workflow in workflows)
        {
            var version = await db.WorkflowVersions.AsNoTracking().FirstAsync(v => v.DefinitionId == workflow.Id && v.Number == workflow.CurrentVersion, cancellationToken);
            section.Add(new XElement(Ns + "Workflow", version.Definition)
                .With("Name", workflow.Name).With("Description", workflow.Description).With("Enabled", workflow.Enabled));
        }

        foreach (var rule in rules)
        {
            section.Add(new XElement(Ns + "Rule", rule.Definition).With("Name", rule.Name).With("Enabled", rule.Enabled));
        }

        return section;
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var workspaceId = context.WorkspaceId!.Value;
        var prefix = context.WorkspaceName;
        foreach (var element in section.Elements(Ns + "Workflow"))
        {
            var name = element.RequiredAttr("Name").Trim();
            var (steps, error) = DefinitionJson.TryParse<WorkflowSteps>(element.Value);
            var errors = error is not null ? [error] : Definitions.ValidateWorkflow(steps, actions);
            if (errors.Count > 0)
            {
                throw new TemplateException($"Workflow '{name}': {string.Join(" ", errors)}", element);
            }

            var description = element.Attr("Description");
            var enabled = element.BoolAttr("Enabled", true);
            var workflow = context.IsPlanned ? null : await db.Workflows.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.Name == name, cancellationToken);
            if (workflow is null)
            {
                context.Created("workflow", $"{prefix}: {name}");
                if (!context.DryRun)
                {
                    WorkflowWriter.Create(db, workspaceId, name, description, enabled, steps!);
                }

                continue;
            }

            var changed = await WorkflowWriter.SetStepsAsync(db, workflow, steps!, cancellationToken);
            if (changed || workflow.Description != description || workflow.Enabled != enabled)
            {
                context.Updated("workflow", $"{prefix}: {name}", changed ? $"version {workflow.CurrentVersion}" : null);
                workflow.Description = description;
                workflow.Enabled = enabled;
            }
        }

        foreach (var element in section.Elements(Ns + "Rule"))
        {
            var name = element.RequiredAttr("Name").Trim();
            var (definition, error) = DefinitionJson.TryParse<RuleDefinition>(element.Value);
            var errors = error is not null ? [error] : Definitions.ValidateRule(definition, triggers.Keys, actions);
            if (errors.Count > 0)
            {
                throw new TemplateException($"Rule '{name}': {string.Join(" ", errors)}", element);
            }

            var json = DefinitionJson.Serialize(definition);
            var enabled = element.BoolAttr("Enabled", true);
            var rule = context.IsPlanned ? null : await db.Rules.FirstOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Name == name, cancellationToken);
            if (rule is null)
            {
                context.Created("rule", $"{prefix}: {name}");
                if (!context.DryRun)
                {
                    db.Rules.Add(new AutomationRule { Id = Ids.New(), WorkspaceId = workspaceId, Name = name, Enabled = enabled, Trigger = definition!.Trigger.Type, Definition = json });
                }
            }
            else if (rule.Definition != json || rule.Enabled != enabled)
            {
                context.Updated("rule", $"{prefix}: {name}");
                rule.Definition = json;
                rule.Trigger = definition!.Trigger.Type;
                rule.Enabled = enabled;
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
