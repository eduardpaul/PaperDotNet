using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Data;

namespace PaperDotNet.Taxonomy.Features;

/// <summary>
/// Template section <c>TermGroups</c> (PRV-01/02): term groups, term sets and term trees with labels and synonyms. The
/// system group (keywords) is not part of templates. Terms are matched by name among their siblings; terms missing from
/// the template stay. Registers term sets as <c>Group/Set</c>.
/// </summary>
internal sealed class TermGroupTemplateHandler(TaxonomyDbContext db) : ITemplateHandler
{
    private static readonly XName TermName = TemplateXml.Name("Term");

    public XName Element => TemplateXml.Name("TermGroups");

    public TemplateLevel Level => TemplateLevel.Tenant;

    public int Order => 300;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = context.TenantId;
        var ct = cancellationToken;
        var groups = await database.Groups.AsNoTracking().Where(g => g.TenantId == tenant && !g.IsSystem).OrderBy(g => g.Name).ToListAsync(ct);
        var names = groups.ToDictionary(g => g.Id, g => g.Name);
        var sets = (await database.TermSets.AsNoTracking().Where(s => s.TenantId == tenant && !s.IsKeywords).OrderBy(s => s.Name).ToListAsync(ct))
            .Where(s => names.ContainsKey(s.GroupId) && context.Includes(TemplateKinds.TermSet, $"{names[s.GroupId]}/{s.Name}"))
            .ToList();
        if (sets.Count == 0)
        {
            return null;
        }

        var setIds = sets.Select(s => s.Id).ToHashSet();
        var terms = (await database.Terms.AsNoTracking().Where(t => t.TenantId == tenant && t.MergedIntoId == null).ToListAsync(ct))
            .Where(t => setIds.Contains(t.TermSetId))
            .ToLookup(t => (t.TermSetId, t.ParentId));

        IEnumerable<XElement> Tree(Guid setId, Guid? parentId) => terms[(setId, parentId)]
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new XElement(TermName,
                    t.GetLabels().OrderBy(l => l.Language, StringComparer.Ordinal).Select(l => new XElement(TemplateXml.Name("Label"), new XAttribute("Language", l.Language), new XAttribute("Name", l.Name))),
                    t.GetSynonyms().Select(s => new XElement(TemplateXml.Name("Synonym"), s)),
                    Tree(setId, t.Id))
                .With("Name", t.Name).With("Description", t.Description).With("Color", t.Color)
                .With("SortOrder", t.SortOrder == 0 ? null : t.SortOrder).With("Deprecated", t.IsDeprecated, omitDefault: true));

        return new XElement(Element, groups.Where(g => sets.Any(s => s.GroupId == g.Id)).Select(g => new XElement(TemplateXml.Name("TermGroup"),
                sets.Where(s => s.GroupId == g.Id).Select(s => new XElement(TemplateXml.Name("TermSet"), Tree(s.Id, null))
                    .With("Name", s.Name).With("Description", s.Description).With("Open", s.IsOpen, omitDefault: true)))
            .With("Name", g.Name).With("Description", g.Description)));
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var tenantId = context.TenantId;
        foreach (var groupElement in section.Elements(TemplateXml.Name("TermGroup")))
        {
            var groupName = groupElement.RequiredAttr("Name").Trim();
            var database = db;
            var tenant = tenantId;
            var wantedGroup = groupName;
            var ct = cancellationToken;
            var group = await database.Groups.FirstOrDefaultAsync(g => g.TenantId == tenant && g.Name == wantedGroup, ct);
            if (group is { IsSystem: true })
            {
                throw new TemplateException($"Term group '{groupName}' is the system group and cannot be provisioned.", groupElement);
            }

            var description = groupElement.Attr("Description");
            var newGroup = group is null;
            if (group is null)
            {
                group = new TermGroup { Id = Ids.New(), TenantId = tenantId, Name = groupName, Description = description };
                context.Created(TemplateKinds.TermGroup, groupName);
                if (!context.DryRun)
                {
                    db.Groups.Add(group);
                }
            }
            else if (group.Description != description)
            {
                context.Updated(TemplateKinds.TermGroup, groupName, "description");
                group.Description = description;
            }

            foreach (var setElement in groupElement.Elements(TemplateXml.Name("TermSet")))
            {
                var setName = setElement.RequiredAttr("Name").Trim();
                var key = $"{groupName}/{setName}";
                var groupId = group.Id;
                var wantedSet = setName;
                var set = newGroup ? null : await database.TermSets.FirstOrDefaultAsync(s => s.TenantId == tenant && s.GroupId == groupId && s.Name == wantedSet, ct);
                var open = setElement.BoolAttr("Open", false);
                var setDescription = setElement.Attr("Description");
                var newSet = set is null;
                if (set is null)
                {
                    set = new TermSet { Id = Ids.New(), TenantId = tenantId, GroupId = group.Id, Name = setName, Description = setDescription, IsOpen = open };
                    context.Created(TemplateKinds.TermSet, key);
                    if (!context.DryRun)
                    {
                        db.TermSets.Add(set);
                    }
                }
                else if (set.Description != setDescription || set.IsOpen != open)
                {
                    context.Updated(TemplateKinds.TermSet, key, set.IsOpen != open ? (open ? "opened" : "closed") : "description");
                    set.Description = setDescription;
                    set.IsOpen = open;
                }

                context.Register(TemplateKinds.TermSet, key, set.Id);
                var setId = set.Id;
                var existing = newSet ? [] : await database.Terms.Where(t => t.TenantId == tenant && t.TermSetId == setId && t.MergedIntoId == null).ToListAsync(ct);
                ApplyTerms(setElement, tenantId, set.Id, null, null, key, existing, context);
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private void ApplyTerms(XElement parentElement, Guid tenantId, Guid setId, Term? parent, string? parentPath, string setKey, List<Term> existing, TemplateContext context)
    {
        foreach (var element in parentElement.Elements(TermName))
        {
            var name = element.RequiredAttr("Name").Trim();
            var path = parentPath is null ? name : $"{parentPath}/{name}";
            var labels = element.Elements(TemplateXml.Name("Label"))
                .Select(l => new TermLabel(l.RequiredAttr("Language").Trim(), l.RequiredAttr("Name").Trim())).ToList();
            if (labels.GroupBy(l => l.Language, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            {
                throw new TemplateException($"Term '{path}': one label per language.", element);
            }

            var synonyms = element.Elements(TemplateXml.Name("Synonym")).Select(s => s.Value.Trim()).Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var normalized = TermRules.Normalize(name);
            var term = existing.FirstOrDefault(t => t.ParentId == parent?.Id && t.NormalizedName == normalized);
            var description = element.Attr("Description");
            var color = element.Attr("Color")?.ToLowerInvariant();
            var sortOrder = element.IntAttr("SortOrder") ?? 0;
            var deprecated = element.BoolAttr("Deprecated", false);
            if (term is null)
            {
                term = TermRules.NewTerm(tenantId, setId, parent?.Id, parent?.Path, name);
                existing.Add(term);
                Set(term, description, color, sortOrder, deprecated, labels, synonyms);
                context.Created(TemplateKinds.Term, $"{setKey}: {path}");
                if (!context.DryRun)
                {
                    db.Terms.Add(term);
                }
            }
            else if (term.Description != description || term.Color != color || term.SortOrder != sortOrder || term.IsDeprecated != deprecated
                || !Same(term.GetLabels().Select(l => $"{l.Language}={l.Name}"), labels.Select(l => $"{l.Language}={l.Name}"))
                || !Same(term.GetSynonyms(), synonyms))
            {
                context.Updated(TemplateKinds.Term, $"{setKey}: {path}");
                Set(term, description, color, sortOrder, deprecated, labels, synonyms);
            }

            context.Register(TemplateKinds.Term, $"{setKey}/{path}", term.Id);
            ApplyTerms(element, tenantId, setId, term, path, setKey, existing, context);
        }
    }

    private static void Set(Term term, string? description, string? color, int sortOrder, bool deprecated, List<TermLabel> labels, List<string> synonyms)
    {
        term.Description = description;
        term.Color = color;
        term.SortOrder = sortOrder;
        term.IsDeprecated = deprecated;
        term.SetLabels(labels);
        term.SetSynonyms(synonyms);
        term.RefreshSearchText();
    }

    private static bool Same(IEnumerable<string> a, IEnumerable<string> b) =>
        a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal);
}
