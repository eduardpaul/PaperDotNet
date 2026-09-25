using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Data;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// Workspace section <c>SmartFolders</c> in <c>urn:paperdotnet:smartfolders:1</c>: the workspace's shared smart folders
/// with their definitions as JSON. Lists are referenced by name already; terms are written as <c>Group/Set/Term/Child</c>
/// so the section is portable. Folders are matched by name; personal folders are not exported.
/// </summary>
internal sealed class SmartFolderTemplateHandler(ListsDbContext db, ITermStore terms) : ITemplateHandler
{
    public static readonly XNamespace Ns = "urn:paperdotnet:smartfolders:1";

    public XName Element => Ns + "SmartFolders";

    public TemplateLevel Level => TemplateLevel.Workspace;

    public int Order => 450;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var folders = await db.SmartFolders.AsNoTracking()
            .Where(f => f.WorkspaceId == context.WorkspaceId && f.OwnerId == null)
            .OrderBy(f => f.Name)
            .ToListAsync(cancellationToken);
        if (folders.Count == 0)
        {
            return null;
        }

        var section = new XElement(Element);
        foreach (var folder in folders)
        {
            var definition = JsonNode.Parse(folder.Definition)!.AsObject();
            if (definition["terms"] is JsonArray { Count: > 0 } ids)
            {
                var termIds = ids.Select(i => Guid.Parse(i!.GetValue<string>())).ToList();
                var paths = await terms.GetTermPathsAsync(termIds, cancellationToken);
                definition["terms"] = new JsonArray([.. termIds.Where(paths.ContainsKey).Select(id => (JsonNode?)JsonValue.Create(paths[id]))]);
                foreach (var path in paths.Values)
                {
                    var parts = path.Split('/');
                    context.Require(TemplateKinds.TermSet, $"{parts[0]}/{parts[1]}");
                }
            }

            section.Add(new XElement(Ns + "SmartFolder", definition.ToJsonString()).With("Name", folder.Name).With("Description", folder.Description));
        }

        return section;
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var workspaceId = context.WorkspaceId!.Value;
        foreach (var element in section.Elements(Ns + "SmartFolder"))
        {
            var name = element.RequiredAttr("Name").Trim();
            JsonObject definition;
            try
            {
                definition = JsonNode.Parse(element.Value)?.AsObject() ?? throw new JsonException("Empty definition.");
            }
            catch (JsonException ex)
            {
                throw new TemplateException($"Smart folder '{name}': the definition is not valid JSON ({ex.Message}).", element);
            }

            if (definition["terms"] is JsonArray paths)
            {
                var ids = new JsonArray();
                foreach (var path in paths.Select(p => p?.GetValue<string>() ?? ""))
                {
                    var id = context.Resolve(TemplateKinds.Term, path) ?? await terms.FindTermByPathAsync(path, cancellationToken)
                        ?? throw new TemplateException($"Smart folder '{name}': the term '{path}' does not exist.", element);
                    ids.Add(id.ToString());
                }

                definition["terms"] = ids;
            }

            var json = SmartFolders.Serialize(SmartFolders.ParseDefinition(definition.ToJsonString()));
            var description = element.Attr("Description");
            var folder = context.IsPlanned ? null
                : await db.SmartFolders.FirstOrDefaultAsync(f => f.WorkspaceId == workspaceId && f.OwnerId == null && f.Name == name, cancellationToken);
            if (folder is null)
            {
                context.Created("smartFolder", $"{context.WorkspaceName}: {name}");
                if (!context.DryRun)
                {
                    db.SmartFolders.Add(new SmartFolder { Id = Ids.New(), WorkspaceId = workspaceId, Name = name, Description = description, Definition = json });
                }
            }
            else if (folder.Definition != json || folder.Description != description)
            {
                context.Updated("smartFolder", $"{context.WorkspaceName}: {name}");
                folder.Definition = json;
                folder.Description = description;
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
