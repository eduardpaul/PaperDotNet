using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.ExtensionHost.Runtime;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.ExtensionHost.Features;

/// <summary>
/// Template section <c>Extensions</c> (PRV-01/02): which extensions are enabled and their settings
/// (values as JSON). Runs first, so that later sections can use the extensions' content types and
/// field types. Enabled extensions are registered on the context (extension sections are gated on it).
/// </summary>
internal sealed class ExtensionTemplateHandler(
    ExtensionCatalog catalog, ExtensionsDbContext db, ExtensionState state, IRoleProvisioning roles, IContentTypeProvisioning contentTypes,
    Taxonomy.Contracts.ITermSetProvisioning termSets) : ITemplateHandler
{
    public XName Element => TemplateXml.Name("Extensions");

    public TemplateLevel Level => TemplateLevel.Tenant;

    public int Order => 100;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var rows = await db.TenantExtensions.AsNoTracking().ToDictionaryAsync(e => e.ExtensionId, StringComparer.Ordinal, cancellationToken);
        var elements = new List<XElement>();
        foreach (var extension in catalog.All.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var enabled = await state.IsEnabledAsync(extension.Id, cancellationToken);
            var row = rows.GetValueOrDefault(extension.Id);
            if ((row is null && !enabled) || !context.Includes(TemplateKinds.Extension, extension.Id))
            {
                continue;
            }

            var settings = JsonNode.Parse(row?.Settings ?? "{}") as JsonObject ?? [];
            elements.Add(new XElement(TemplateXml.Name("Extension"),
                    settings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new XElement(TemplateXml.Name("Setting"), new XAttribute("Name", p.Key), p.Value?.ToJsonString() ?? "null")))
                .With("Id", extension.Id).With("Enabled", enabled));
        }

        return elements.Count == 0 ? null : new XElement(Element, elements);
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        foreach (var element in section.Elements(TemplateXml.Name("Extension")))
        {
            var id = element.RequiredAttr("Id");
            var extension = catalog.Find(id) ?? throw new TemplateException($"Extension '{id}' is not installed on this server.", element);
            var settings = new JsonObject();
            foreach (var setting in element.Elements(TemplateXml.Name("Setting")))
            {
                try
                {
                    settings[setting.RequiredAttr("Name")] = JsonNode.Parse(setting.Value);
                }
                catch (JsonException)
                {
                    throw new TemplateException($"Extension '{id}': the value of setting '{setting.Attr("Name")}' is not valid JSON (write text as \"text\").", setting);
                }
            }

            using var document = JsonDocument.Parse(settings.ToJsonString());
            var (stored, _, errors) = ExtensionEndpoints.CheckSettings(extension, document.RootElement);
            if (errors.Count > 0)
            {
                throw new TemplateException($"Extension '{id}': " + string.Join(" ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")), element);
            }

            var enable = element.BoolAttr("Enabled", true);
            var enabled = await state.IsEnabledAsync(id, cancellationToken);
            var row = await db.TenantExtensions.FirstOrDefaultAsync(e => e.ExtensionId == id, cancellationToken);
            if (!JsonNode.DeepEquals(JsonNode.Parse(row?.Settings ?? "{}"), stored))
            {
                context.Updated(TemplateKinds.Extension, id, "settings");
                if (!context.DryRun)
                {
                    row = await ExtensionEndpoints.RowAsync(db, id, cancellationToken, enabledIfNew: enabled);
                    row.Settings = stored.ToJsonString();
                    await ExtensionEndpoints.SaveAsync(db, state, cancellationToken);
                }
            }

            if (enable && !enabled)
            {
                context.Updated(TemplateKinds.Extension, id, "enabled");
                if (!context.DryRun)
                {
                    await ExtensionEndpoints.EnableExtensionAsync(extension, db, roles, contentTypes, termSets, state, cancellationToken);
                }
            }
            else if (!enable && enabled)
            {
                context.Updated(TemplateKinds.Extension, id, "disabled");
                if (!context.DryRun)
                {
                    row = await ExtensionEndpoints.RowAsync(db, id, cancellationToken);
                    row.Enabled = false;
                    await ExtensionEndpoints.SaveAsync(db, state, cancellationToken);
                }
            }

            if (enable)
            {
                context.Register(TemplateKinds.Extension, id, Guid.Empty);
            }
        }
    }
}
