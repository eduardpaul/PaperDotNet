using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.ExtensionHost.Runtime;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.ExtensionHost.Features;

/// <summary>
/// Template section <c>Extensions</c> (PRV-01/02): which extensions are enabled and their settings (values as JSON). Runs
/// first, so that later sections can use the extensions' content types and field types. Enabled extensions are registered
/// on the context (extension sections are gated on it).
/// </summary>
internal sealed class ExtensionTemplateHandler(
    ExtensionCatalog catalog, ExtensionsDbContext db, ExtensionState state, IRoleProvisioning roles, IContentTypeProvisioning contentTypes,
    ITermSetProvisioning termSets) : ITemplateHandler
{
    public XName Element => TemplateXml.Name("Extensions");

    public TemplateLevel Level => TemplateLevel.Tenant;

    public int Order => 100;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = context.TenantId;
        var ct = cancellationToken;
        var rows = (await database.TenantExtensions.AsNoTracking().Where(e => e.TenantId == tenant).ToListAsync(ct)).ToDictionary(e => e.ExtensionId, StringComparer.Ordinal);
        var elements = new List<XElement>();
        foreach (var extension in catalog.All.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var enabled = await state.IsEnabledAsync(context.TenantId, extension.Id, ct);
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
        var tenantId = context.TenantId;
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
            var enabled = await state.IsEnabledAsync(tenantId, id, cancellationToken);
            var database = db;
            var tenant = tenantId;
            var extensionId = id;
            var ct = cancellationToken;
            var row = await database.TenantExtensions.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenant && e.ExtensionId == extensionId, ct);
            if (!JsonNode.DeepEquals(JsonNode.Parse(row?.Settings ?? "{}"), stored))
            {
                context.Updated(TemplateKinds.Extension, id, "settings");
                if (!context.DryRun)
                {
                    var tracked = await ExtensionEndpoints.RowAsync(db, tenantId, id, enabledIfNew: enabled, ct);
                    tracked.Settings = stored.ToJsonString();
                    await ExtensionEndpoints.SaveAsync(db, state, ct);
                }
            }

            if (enable && !enabled)
            {
                context.Updated(TemplateKinds.Extension, id, "enabled");
                if (!context.DryRun)
                {
                    await ExtensionEndpoints.EnableExtensionAsync(tenantId, extension, db, roles, contentTypes, termSets, state, ct);
                }
            }
            else if (!enable && enabled)
            {
                context.Updated(TemplateKinds.Extension, id, "disabled");
                if (!context.DryRun)
                {
                    var tracked = await ExtensionEndpoints.RowAsync(db, tenantId, id, enabledIfNew: false, ct);
                    tracked.Enabled = false;
                    await ExtensionEndpoints.SaveAsync(db, state, ct);
                }
            }

            if (enable)
            {
                context.Register(TemplateKinds.Extension, id, Guid.Empty);
            }
        }
    }
}

/// <summary>
/// Runs an extension's template section only where the extension is enabled, or is being enabled by the same template
/// (its Extensions section registers it).
/// </summary>
internal sealed class GatedTemplateHandler(string extensionId, ITemplateHandler inner, Extensions.IExtensionState state) : ITemplateHandler
{
    public XName Element => inner.Element;

    public TemplateLevel Level => inner.Level;

    public int Order => inner.Order;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken) =>
        await state.IsEnabledAsync(context.TenantId, extensionId, cancellationToken) ? await inner.ExportAsync(context, cancellationToken) : null;

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        if (context.Resolve(TemplateKinds.Extension, extensionId) is not null || await state.IsEnabledAsync(context.TenantId, extensionId, cancellationToken))
        {
            await inner.ApplyAsync(section, context, cancellationToken);
        }
        else
        {
            context.Warn($"Section {section.Name} was skipped: the extension '{extensionId}' is not enabled.", section);
        }
    }
}
