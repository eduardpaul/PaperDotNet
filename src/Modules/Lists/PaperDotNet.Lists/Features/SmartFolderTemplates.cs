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
/// Template section <c>SmartFolders</c> (namespace <c>urn:paperdotnet:smartfolders:1</c>, workspace level): the shared
/// smart folders of a workspace with their definitions as JSON; terms are written as <c>Group/Set/Term</c> paths, so the
/// section is portable. Folders are matched by name; personal folders are not exported.
/// </summary>
internal sealed class SmartFolderTemplateHandler(ListsDbContext db, ITermStore terms) : ITemplateHandler
{
    public static readonly XNamespace Ns = "urn:paperdotnet:smartfolders:1";

    public XName Element => Ns + "SmartFolders";

    public TemplateLevel Level => TemplateLevel.Workspace;

    public int Order => 450;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var folders = await SharedAsync(context, tracking: false, cancellationToken);
        if (folders.Count == 0)
        {
            return null;
        }

        var section = new XElement(Element);
        foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var definition = JsonNode.Parse(folder.Definition)?.AsObject() ?? [];
            if (definition["terms"] is JsonArray { Count: > 0 } ids)
            {
                var termIds = ids.Select(i => i is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
                var paths = await terms.GetTermPathsAsync(context.TenantId, termIds, cancellationToken);
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
        var existing = context.IsPlanned ? [] : await SharedAsync(context, tracking: true, cancellationToken);
        foreach (var element in section.Elements(Ns + "SmartFolder"))
        {
            var name = element.RequiredAttr("Name").Trim();
            JsonObject definition;
            try
            {
                definition = JsonNode.Parse(element.Value)?.AsObject() ?? throw new JsonException("Empty definition.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new TemplateException($"Smart folder '{name}': the definition is not valid JSON ({ex.Message}).", element);
            }

            if (definition["terms"] is JsonArray paths)
            {
                var ids = new JsonArray();
                foreach (var path in paths.Select(p => p is JsonValue v && v.TryGetValue<string>(out var text) ? text : ""))
                {
                    var id = context.Resolve(TemplateKinds.Term, path) ?? await terms.FindTermByPathAsync(context.TenantId, path, cancellationToken)
                        ?? throw new TemplateException($"Smart folder '{name}': the term '{path}' does not exist.", element);
                    ids.Add((JsonNode)JsonValue.Create(id.ToString()));
                }

                definition["terms"] = ids;
            }

            string json;
            try
            {
                json = SmartFolders.Serialize(JsonSerializer.Deserialize(definition.ToJsonString(), ListsJson.Default.SmartFolderDefinition) ?? new SmartFolderDefinition());
            }
            catch (JsonException ex)
            {
                throw new TemplateException($"Smart folder '{name}': the definition is not valid ({ex.Message}).", element);
            }

            var description = element.Attr("Description");
            var folder = existing.FirstOrDefault(f => f.Name == name);
            if (folder is null)
            {
                context.Created("smartFolder", $"{context.WorkspaceName}: {name}");
                if (!context.DryRun)
                {
                    db.SmartFolders.Add(new SmartFolder { Id = Ids.New(), TenantId = context.TenantId, WorkspaceId = workspaceId, Name = name, Description = description, Definition = json });
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

    private async Task<List<SmartFolder>> SharedAsync(TemplateContext context, bool tracking, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = context.TenantId;
        Guid? workspace = context.WorkspaceId;
        var ct = cancellationToken;
        return tracking
            ? await database.SmartFolders.Where(f => f.TenantId == tenant && f.WorkspaceId == workspace && f.OwnerId == null).ToListAsync(ct)
            : await database.SmartFolders.AsNoTracking().Where(f => f.TenantId == tenant && f.WorkspaceId == workspace && f.OwnerId == null).ToListAsync(ct);
    }
}
