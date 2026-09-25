using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Automation.Data;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Automation.Features;

/// <summary>
/// Workspace section <c>Automations</c> in <c>urn:paperdotnet:automation:2</c>: the workspace's automations with their
/// definitions as JSON (they refer to lists and users by name, so they are portable). Automations are matched by
/// name; a changed definition becomes a new version.
/// </summary>
internal sealed class AutomationTemplateHandler(AutomationDbContext db, TriggerCatalog triggers, ActionCatalog actions) : ITemplateHandler
{
    public static readonly XNamespace Ns = "urn:paperdotnet:automation:2";

    public XName Element => Ns + "Automations";

    public TemplateLevel Level => TemplateLevel.Workspace;

    public int Order => 500;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var automations = await db.Automations.AsNoTracking().Where(a => a.WorkspaceId == context.WorkspaceId).OrderBy(a => a.Name).ToListAsync(cancellationToken);
        if (automations.Count == 0)
        {
            return null;
        }

        var section = new XElement(Element);
        foreach (var automation in automations)
        {
            var version = await db.Versions.AsNoTracking().FirstAsync(v => v.AutomationId == automation.Id && v.Number == automation.CurrentVersion, cancellationToken);
            section.Add(new XElement(Ns + "Automation", version.Definition)
                .With("Name", automation.Name).With("Description", automation.Description).With("Enabled", automation.Enabled));
        }

        return section;
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var workspaceId = context.WorkspaceId!.Value;
        var prefix = context.WorkspaceName;
        foreach (var element in section.Elements(Ns + "Automation"))
        {
            var name = element.RequiredAttr("Name").Trim();
            var (spec, error) = DefinitionJson.TryParse<AutomationSpec>(element.Value);
            var errors = error is not null ? [error] : Definitions.Validate(spec, triggers.Keys, actions);
            if (errors.Count > 0)
            {
                throw new TemplateException($"Automation '{name}': {string.Join(" ", errors)}", element);
            }

            var description = element.Attr("Description");
            var enabled = element.BoolAttr("Enabled", true);
            var automation = context.IsPlanned ? null : await db.Automations.FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.Name == name, cancellationToken);
            if (automation is null)
            {
                context.Created("automation", $"{prefix}: {name}");
                if (!context.DryRun)
                {
                    AutomationWriter.Create(db, workspaceId, name, description, enabled, spec!);
                }

                continue;
            }

            var changed = await AutomationWriter.SetSpecAsync(db, automation, spec!, cancellationToken);
            if (changed || automation.Description != description || automation.Enabled != enabled)
            {
                context.Updated("automation", $"{prefix}: {name}", changed ? $"version {automation.CurrentVersion}" : null);
                automation.Description = description;
                automation.Enabled = enabled;
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
