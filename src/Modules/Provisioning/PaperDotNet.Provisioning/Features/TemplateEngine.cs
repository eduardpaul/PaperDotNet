using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Provisioning.Features;

/// <summary>Outcome of an apply: the changes made (or planned, in a dry run) and warnings.</summary>
public sealed record TemplateResult(bool DryRun, IReadOnlyList<TemplateChange> Changes, IReadOnlyList<string> Warnings);

/// <summary>
/// Exports and applies templates (PRV-01/02) by running the sections of every module in order.
/// Apply always runs a dry run first and writes only when it succeeds.
/// </summary>
internal sealed class TemplateEngine(ITenantScopeFactory scopes, ITenantContext tenant, ICurrentUser user)
{
    private static readonly XName WorkspacesName = TemplateXml.Name("Workspaces");
    private static readonly XName WorkspaceName = TemplateXml.Name("Workspace");
    private static readonly XName ListsName = TemplateXml.Name("Lists");
    private static readonly XName ListName = TemplateXml.Name("List");

    /// <summary>
    /// Every run gets its own scope (and so its own DbContexts), so that a dry run leaves no tracked
    /// changes behind for the real run.
    /// </summary>
    private async Task<T> InScopeAsync<T>(Func<Sections, Task<T>> run)
    {
        await using var scope = scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!, user.UserId);
        var containers = scope.ServiceProvider.GetServices<ITemplateContainer>().ToList();
        return await run(new Sections(
            [.. scope.ServiceProvider.GetServices<ITemplateHandler>()],
            containers.Single(c => c.Level == TemplateLevel.Workspace),
            containers.Single(c => c.Level == TemplateLevel.List)));
    }

    private sealed record Sections(IReadOnlyList<ITemplateHandler> Handlers, ITemplateContainer Workspaces, ITemplateContainer Lists)
    {
        public IEnumerable<ITemplateHandler> Level(TemplateLevel level) =>
            Handlers.Where(h => h.Level == level).OrderBy(h => h.Order).ThenBy(h => h.Element.ToString(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Exports the tenant, or one workspace with the tenant-level objects it depends on; with a
    /// <paramref name="package"/>, sections also write their content (items, files) into it (PRV-04).
    /// </summary>
    public Task<XDocument> ExportAsync(Guid? workspaceId, ITemplatePackage? package, CancellationToken ct) =>
        InScopeAsync(sections => ExportAsync(sections, workspaceId, package, ct));

    private static async Task<XDocument> ExportAsync(Sections sections, Guid? workspaceId, ITemplatePackage? package, CancellationToken ct)
    {
        var scope = workspaceId is null ? TemplateScope.Tenant : TemplateScope.Workspace;
        var context = new TemplateContext(scope, dryRun: false) { WorkspaceId = workspaceId, Package = package };

        // Workspaces first: in a workspace template they decide which tenant-level objects are needed.
        var workspaces = new List<XElement>();
        foreach (var id in await sections.Workspaces.ListAsync(context, ct))
        {
            context.WorkspaceId = id;
            context.ListId = null;
            var workspace = await sections.Workspaces.ExportAsync(id, context, ct);
            workspace.Add(await ExportSectionsAsync(sections, TemplateLevel.Workspace, context, ct));
            var lists = new XElement(ListsName);
            foreach (var listId in await sections.Lists.ListAsync(context, ct))
            {
                context.ListId = listId;
                var list = await sections.Lists.ExportAsync(listId, context, ct);
                list.Add(await ExportSectionsAsync(sections, TemplateLevel.List, context, ct));
                lists.Add(list);
            }

            if (lists.HasElements)
            {
                workspace.Add(lists);
            }

            workspaces.Add(workspace);
        }

        context.WorkspaceId = null;
        context.ListId = null;
        var root = new XElement(TemplateXml.Name("Template"), new XAttribute("SchemaVersion", TemplateXml.SchemaVersion), new XAttribute("Scope", scope.ToString()));
        root.Add(await ExportSectionsAsync(sections, TemplateLevel.Tenant, context, ct));
        if (workspaces.Count > 0)
        {
            root.Add(new XElement(WorkspacesName, workspaces));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    /// <summary>
    /// Sections of one level in document order: built-in ones by order, then other namespaces. Exported
    /// from the highest order down, so that sections can require what lower ones (e.g. term sets) export.
    /// </summary>
    private static async Task<List<XElement>> ExportSectionsAsync(Sections all, TemplateLevel level, TemplateContext context, CancellationToken ct)
    {
        var sections = new List<(ITemplateHandler Handler, XElement Section)>();
        foreach (var handler in all.Level(level).Reverse())
        {
            if (await handler.ExportAsync(context, ct) is { } section)
            {
                sections.Add((handler, section));
            }
        }

        return [.. sections
            .OrderBy(s => s.Section.Name.Namespace == TemplateXml.Ns ? 0 : 1)
            .ThenBy(s => s.Handler.Order)
            .Select(s => s.Section)];
    }

    /// <summary>Validates and applies a template (substituted and schema-valid); throws <see cref="TemplateException"/>.</summary>
    public async Task<TemplateResult> ApplyAsync(XElement root, Guid? targetWorkspaceId, bool dryRun, CancellationToken ct, ITemplatePackage? package = null)
    {
        var scope = root.EnumAttr("Scope", TemplateScope.Tenant);
        var workspaceCount = root.Element(WorkspacesName)?.Elements(WorkspaceName).Count() ?? 0;
        if (targetWorkspaceId is not null && workspaceCount != 1)
        {
            throw new TemplateException("A template applied to a workspace needs exactly one Workspace element.", root);
        }

        var plan = await InScopeAsync(sections => RunAsync(sections, root, scope, targetWorkspaceId, dryRun: true, package, ct));
        return dryRun ? plan : await InScopeAsync(sections => RunAsync(sections, root, scope, targetWorkspaceId, dryRun: false, package, ct));
    }

    private static async Task<TemplateResult> RunAsync(
        Sections sections, XElement root, TemplateScope scope, Guid? targetWorkspaceId, bool dryRun, ITemplatePackage? package, CancellationToken ct)
    {
        var context = new TemplateContext(scope, dryRun) { Package = package };
        await ApplySectionsAsync(sections, root, TemplateLevel.Tenant, context, [TemplateXml.Name("Description"), TemplateXml.Name("Parameters"), WorkspacesName], ct);
        foreach (var workspace in root.Element(WorkspacesName)?.Elements(WorkspaceName) ?? [])
        {
            context.WorkspaceId = targetWorkspaceId;
            context.WorkspaceName = null;
            context.ListId = null;
            context.IsPlanned = false;
            await sections.Workspaces.ApplyAsync(workspace, context, ct);
            await ApplySectionsAsync(sections, workspace, TemplateLevel.Workspace, context, [TemplateXml.Name("Members"), ListsName], ct);
            var workspacePlanned = context.IsPlanned;
            foreach (var list in workspace.Element(ListsName)?.Elements(ListName) ?? [])
            {
                context.ListId = null;
                context.IsPlanned = workspacePlanned;
                await sections.Lists.ApplyAsync(list, context, ct);
                await ApplySectionsAsync(sections, list, TemplateLevel.List, context, [TemplateXml.Name("ContentTypes"), TemplateXml.Name("Views"), TemplateXml.Name("Permissions")], ct);
            }
        }

        await context.RunDeferredAsync(ct);
        return new TemplateResult(dryRun, context.Changes, context.Warnings);
    }

    private static async Task ApplySectionsAsync(Sections all, XElement parent, TemplateLevel level, TemplateContext context, IReadOnlyCollection<XName> ownChildren, CancellationToken ct)
    {
        var levelHandlers = all.Level(level).ToList();
        foreach (var handler in levelHandlers)
        {
            if (parent.Element(handler.Element) is { } section)
            {
                await handler.ApplyAsync(section, context, ct);
            }
        }

        // Sections nobody handles: an extension that is not installed (or not enabled) here.
        var known = levelHandlers.Select(h => h.Element).Concat(ownChildren).ToHashSet();
        foreach (var unknown in parent.Elements().Where(e => !known.Contains(e.Name)))
        {
            context.Warn($"Section {unknown.Name} was skipped: no module or enabled extension handles it.", unknown);
        }
    }
}

/// <summary>Reads templates safely, replaces parameters and validates against the schema (PRV-03).</summary>
internal static partial class TemplateReader
{
    private const string SchemaResource = "PaperDotNet.Provisioning.Schema.template-1.0.xsd";
    private static readonly Lazy<string> SchemaText = new(() =>
    {
        using var stream = typeof(TemplateReader).Assembly.GetManifestResourceStream(SchemaResource)
            ?? throw new InvalidOperationException($"Missing resource {SchemaResource}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<XmlSchemaSet> Schemas = new(() =>
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(SchemaText.Value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        set.Add(XmlSchema.Read(reader, null)!);
        set.Compile();
        return set;
    });

    public static string Schema => SchemaText.Value;

    /// <summary>Parses without DTDs or external resources, keeping line numbers.</summary>
    public static XDocument Parse(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, MaxCharactersInDocument = 20_000_000 };
        using var reader = XmlReader.Create(stream, settings);
        try
        {
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new TemplateException(ex.LineNumber > 0 ? $"Line {ex.LineNumber}: {ex.Message}" : ex.Message);
        }
    }

    /// <summary>
    /// Replaces <c>{parameter:Name}</c> in attributes and text with the given values or the declared
    /// defaults. Returns problems (undeclared parameters, missing values).
    /// </summary>
    public static List<string> Substitute(XElement root, IReadOnlyDictionary<string, string> values, ICollection<string> warnings)
    {
        var errors = new List<string>();
        var declared = new Dictionary<string, string?>(StringComparer.Ordinal);
        var parameters = root.Element(TemplateXml.Name("Parameters"));
        foreach (var parameter in parameters?.Elements(TemplateXml.Name("Parameter")) ?? [])
        {
            if (parameter.Attr("Name") is { } name)
            {
                declared[name] = values.TryGetValue(name, out var value) ? value : parameter.Attr("Default");
            }
        }

        foreach (var name in values.Keys.Where(k => !declared.ContainsKey(k)))
        {
            warnings.Add($"Parameter '{name}' is not declared by the template and was ignored.");
        }

        string Replace(string text, XObject source) => Token().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (!declared.TryGetValue(name, out var value))
            {
                errors.Add(TemplateException.Describe($"Parameter '{name}' is not declared.", source));
                return match.Value;
            }

            if (value is null)
            {
                errors.Add(TemplateException.Describe($"Parameter '{name}' needs a value.", source));
                return match.Value;
            }

            return value;
        });

        foreach (var element in root.DescendantsAndSelf().Where(e => parameters is null || e.Parent != parameters))
        {
            foreach (var attribute in element.Attributes().Where(a => a.Value.Contains("{parameter:", StringComparison.Ordinal)))
            {
                attribute.Value = Replace(attribute.Value, attribute);
            }

            foreach (var text in element.Nodes().OfType<XText>().Where(t => t.Value.Contains("{parameter:", StringComparison.Ordinal)))
            {
                text.Value = Replace(text.Value, element);
            }
        }

        return errors;
    }

    /// <summary>Schema errors with line numbers; sections in other namespaces are validated laxly.</summary>
    public static List<string> Validate(XDocument document)
    {
        var errors = new List<string>();
        document.Validate(Schemas.Value, (sender, e) =>
        {
            if (e.Severity == XmlSeverityType.Error)
            {
                errors.Add(TemplateException.Describe(e.Message, sender as XObject));
            }
        });
        return errors;
    }

    [GeneratedRegex(@"\{parameter:([A-Za-z][A-Za-z0-9_]*)\}")]
    private static partial Regex Token();
}
