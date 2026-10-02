using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Portable relationship semantics, using taxonomy paths rather than tenant-local ids.</summary>
internal sealed class RelationshipTypesTemplateHandler(ListsDbContext db, ITermStore terms, RelationshipTypes types) : ITemplateHandler
{
    public static readonly XNamespace Ns = "urn:paperdotnet:relationships:1";
    public XName Element => Ns + "Types";
    public TemplateLevel Level => TemplateLevel.Tenant;
    public int Order => 350;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken ct)
    {
        var definitions = await db.RelationshipTypes.AsNoTracking().ToListAsync(ct);
        var paths = await terms.GetTermPathsAsync(definitions.Select(t => t.Id).ToArray(), ct);
        var entries = new List<XElement>();
        foreach (var definition in definitions)
        {
            if (!paths.TryGetValue(definition.Id, out var path)) continue;
            var parts = path.Split('/');
            var set = $"{parts[0]}/{parts[1]}";
            if (!context.Includes(TemplateKinds.TermSet, set)) continue;
            context.Require(TemplateKinds.TermSet, set);
            entries.Add(new XElement(Ns + "Type").With("Term", path).With("Directed", definition.Directed, omitDefault: true)
                .With("InverseLabel", definition.InverseLabel).With("MaxIncoming", definition.MaxIncoming).With("MaxOutgoing", definition.MaxOutgoing));
        }
        return entries.Count == 0 ? null : new XElement(Element, entries);
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken ct)
    {
        foreach (var element in section.Elements(Ns + "Type"))
        {
            var name = element.RequiredAttr("Term");
            var termId = context.Resolve(TemplateKinds.Term, name) ?? await terms.FindTermByPathAsync(name, ct);
            // A plain name creates a predicate in the open relationship vocabulary.
            if (name.Contains('/') && termId is null) throw new TemplateException($"Relationship type '{name}' was not found.", element);
            int? Limit(string attribute) => element.IntAttr(attribute);
            var options = new RelationshipTypeOptions(termId?.ToString() ?? name, element.BoolAttr("Directed", false), element.Attr("InverseLabel"), Limit("MaxIncoming"), Limit("MaxOutgoing"));
            try { RelationshipTypes.Validate(options); }
            catch (ArgumentException ex) { throw new TemplateException(ex.Message, element); }
            if (context.DryRun)
            {
                context.Created("relationshipType", name);
                continue;
            }
            try { await types.EnsureAsync(options, ct); }
            catch (ArgumentException ex) { throw new TemplateException(ex.Message, element); }
        }
    }
}
