using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Lists.Templates;
using PaperDotNet.Messaging;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;


/// <summary>
/// The <c>List</c> element of templates (PRV-01/02): settings, content types (by name or key), views
/// and list permissions. Matched by name in the workspace; system lists are never part of templates.
/// Content types and views are added or updated, never removed; when <c>Permissions</c> is present the
/// list's inheritance and grants are made to match it.
/// </summary>
internal sealed class ListTemplateContainer(
    ListsDbContext db,
    ListTemplateRegistry templates,
    ContentTypeProvisioner provisioner,
    IExtensionAvailability extensions,
    ItemQueryRunner runner,
    IUserDirectory users,
    IOutbox outbox,
    ITenantContext tenant,
    ICurrentUser user) : ITemplateContainer
{
    private static readonly ListAccess FullAccess = new(WorkspaceAccessLevel.Manage, fullControl: true, new Dictionary<Guid, WorkspaceAccessLevel>());

    public TemplateLevel Level => TemplateLevel.List;

    public async Task<IReadOnlyList<Guid>> ListAsync(TemplateContext context, CancellationToken cancellationToken) =>
        await db.Lists.AsNoTracking().Where(l => l.WorkspaceId == context.WorkspaceId && l.SystemKey == null)
            .OrderBy(l => l.Name).Select(l => l.Id).ToListAsync(cancellationToken);

    public async Task<XElement> ExportAsync(Guid id, TemplateContext context, CancellationToken cancellationToken)
    {
        var list = await db.Lists.AsNoTracking().FirstAsync(l => l.Id == id, cancellationToken);
        context.ListName = list.Name;
        var contentTypes = await db.ContentTypes.AsNoTracking().Where(c => list.ContentTypeIds.Contains(c.Id)).ToListAsync(cancellationToken);
        var refs = new XElement(TemplateXml.Name("ContentTypes"));
        foreach (var contentType in list.ContentTypeIds.Select(ctId => contentTypes.FirstOrDefault(c => c.Id == ctId)).OfType<ContentType>())
        {
            context.Require(TemplateKinds.ContentType, contentType.Name);
            refs.Add(contentType.Key is { } key
                ? new XElement(TemplateXml.Name("ContentTypeRef"), new XAttribute("Key", key))
                : new XElement(TemplateXml.Name("ContentTypeRef"), new XAttribute("Name", contentType.Name)));
        }

        var views = await db.Views.AsNoTracking().Where(v => v.ListId == id).OrderBy(v => v.Name).ToListAsync(cancellationToken);
        var element = new XElement(TemplateXml.Name("List"),
                refs,
                views.Count == 0 ? null : new XElement(TemplateXml.Name("Views"), views.Select(v => new XElement(TemplateXml.Name("View"),
                        v.Columns.Select(c => new XElement(TemplateXml.Name("Column"), c)))
                    .With("Name", v.Name).With("Layout", v.Layout).With("Default", v.IsDefault, omitDefault: true)
                    .With("Filter", v.Filter).With("OrderBy", v.OrderBy).With("GroupBy", v.GroupBy))),
                await ExportPermissionsAsync(list, context, cancellationToken))
            .With("Name", list.Name)
            .With("Description", list.Description)
            .With("Kind", list.Kind == ListKind.Library ? list.Kind : null)
            .With("AllowFolders", list.AllowFolders ? null : false)
            .With("Versioning", list.Versioning)
            .With("MaxVersions", list.MaxVersions == ListDefinition.DefaultMaxVersions ? null : list.MaxVersions)
            .With("Template", list.TemplateKey);
        return element;
    }

    private async Task<XElement> ExportPermissionsAsync(ListDefinition list, TemplateContext context, CancellationToken ct)
    {
        var element = new XElement(TemplateXml.Name("Permissions")).With("Unique", list.HasUniquePermissions);
        if (!list.HasUniquePermissions)
        {
            return element;
        }

        var grants = await db.Grants.AsNoTracking().Where(g => g.ObjectId == list.Id).ToListAsync(ct);
        var userNames = await users.GetUserNamesAsync([.. grants.Where(g => g.PrincipalType == PrincipalType.User).Select(g => g.PrincipalId)], ct);
        var groupNames = await users.GetGroupNamesAsync([.. grants.Where(g => g.PrincipalType == PrincipalType.Group).Select(g => g.PrincipalId)], ct);
        foreach (var grant in grants)
        {
            var (attribute, name) = grant.PrincipalType == PrincipalType.User
                ? ("User", userNames.GetValueOrDefault(grant.PrincipalId))
                : ("Group", groupNames.GetValueOrDefault(grant.PrincipalId));
            if (name is null)
            {
                continue;
            }

            if (grant.PrincipalType == PrincipalType.Group)
            {
                context.Require(TemplateKinds.Group, name);
            }

            element.Add(new XElement(TemplateXml.Name("Grant"), new XAttribute(attribute, name), new XAttribute("Level", grant.Level.ToString())));
        }

        element.ReplaceNodes(element.Elements().OrderBy(e => e.ToString(), StringComparer.Ordinal).ToList());
        return element;
    }

    public async Task ApplyAsync(XElement element, TemplateContext context, CancellationToken cancellationToken)
    {
        var name = element.RequiredAttr("Name").Trim();
        var key = $"{context.WorkspaceName}/{name}";
        var contentTypes = await ContentTypesAsync(element, context, cancellationToken);
        var list = context.IsPlanned
            ? null
            : await db.Lists.FirstOrDefaultAsync(l => l.WorkspaceId == context.WorkspaceId && l.Name == name, cancellationToken);
        if (list is { SystemKey: not null })
        {
            throw new TemplateException($"List '{name}' is a system list and cannot be provisioned.", element);
        }

        var kind = element.EnumAttr("Kind", ListKind.List);
        var templateKey = element.Attr("Template");
        if (templateKey is not null && templates.FindList(templateKey) is null)
        {
            context.Warn($"List template '{templateKey}' is not known on this server; list '{name}' is created without it.", element);
            templateKey = null;
        }

        var planned = context.IsPlanned;
        if (list is null)
        {
            list = new ListDefinition
            {
                Id = Ids.New(),
                WorkspaceId = context.WorkspaceId!.Value,
                Name = name,
                Description = element.Attr("Description"),
                Kind = kind,
                AllowFolders = element.BoolAttr("AllowFolders", true),
                ContentTypeIds = [.. contentTypes.Select(c => c.Id)],
                Versioning = element.EnumAttr("Versioning", kind == ListKind.Library ? ListVersioning.Major : ListVersioning.Off),
                MaxVersions = element.IntAttr("MaxVersions") ?? ListDefinition.DefaultMaxVersions,
                TemplateKey = templateKey,
            };
            CheckConflicts(contentTypes, name, element);
            context.Created(TemplateKinds.List, key);
            if (context.DryRun)
            {
                planned = true;
            }
            else
            {
                db.Lists.Add(list);
            }
        }
        else
        {
            if (list.Kind != kind)
            {
                throw new TemplateException($"List '{key}' is a {list.Kind} and cannot become a {kind}.", element);
            }

            var changes = new List<string>();
            void Change<T>(string what, T current, T wanted, Action<T> set)
            {
                if (!EqualityComparer<T>.Default.Equals(current, wanted))
                {
                    changes.Add(what);
                    set(wanted);
                }
            }

            Change("description", list.Description, element.Attr("Description"), v => list.Description = v);
            Change("allowFolders", list.AllowFolders, element.BoolAttr("AllowFolders", true), v => list.AllowFolders = v);
            if (element.Attr("Versioning") is not null)
            {
                Change("versioning", list.Versioning, element.EnumAttr("Versioning", list.Versioning), v => list.Versioning = v);
            }

            if (element.IntAttr("MaxVersions") is { } maxVersions)
            {
                Change("maxVersions", list.MaxVersions, maxVersions, v => list.MaxVersions = v);
            }

            var current = await db.ContentTypes.AsNoTracking().Where(c => list.ContentTypeIds.Contains(c.Id)).ToListAsync(cancellationToken);
            foreach (var contentType in contentTypes.Where(c => !list.ContentTypeIds.Contains(c.Id)))
            {
                if (ListSchema.FindConflict(current.SelectMany(c => c.Fields), contentType) is { } conflict)
                {
                    throw new TemplateException($"List '{key}': {conflict}", element);
                }

                current.Add(contentType);
                list.ContentTypeIds = [.. list.ContentTypeIds, contentType.Id];
                changes.Add($"content type added: {contentType.Name}");
            }

            if (changes.Count > 0)
            {
                context.Updated(TemplateKinds.List, key, string.Join(", ", changes));
            }

            contentTypes = [.. current];
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        context.ListId = list.Id;
        context.ListName = name;
        context.IsPlanned = planned;
        context.Register(TemplateKinds.List, key, list.Id);

        var schema = new ListSchema(list, contentTypes, FullAccess);
        await ApplyViewsAsync(element.Element(TemplateXml.Name("Views")), schema, key, planned, context, cancellationToken);
        if (element.Element(TemplateXml.Name("Permissions")) is { } permissions)
        {
            await ApplyPermissionsAsync(permissions, list, key, planned, context, cancellationToken);
        }
    }

    /// <summary>The list's content types: from this template run, the tenant, or provisioned from a built-in or extension key.</summary>
    private async Task<List<ContentType>> ContentTypesAsync(XElement element, TemplateContext context, CancellationToken ct)
    {
        var state = ListsTemplateState.Of(context);
        var result = new List<ContentType>();
        foreach (var reference in element.Element(TemplateXml.Name("ContentTypes"))?.Elements(TemplateXml.Name("ContentTypeRef")) ?? [])
        {
            ContentType? contentType;
            if (reference.Attr("Key") is { } key)
            {
                contentType = state.ByKey.GetValueOrDefault(key) ?? await db.ContentTypes.FirstOrDefaultAsync(c => c.Key == key, ct);
                if (contentType is null)
                {
                    var template = templates.FindContentType(key) ?? throw new TemplateException($"Content type key '{key}' is not known on this server.", reference);
                    if (template.ExtensionId is { } owner && context.Resolve(TemplateKinds.Extension, owner) is null && !await extensions.IsEnabledAsync(owner, ct))
                    {
                        throw new TemplateException($"Content type '{key}' needs the extension '{owner}'.", reference);
                    }

                    contentType = context.DryRun
                        ? new ContentType { Id = Ids.New(), Name = template.Name, Key = key, IsBuiltIn = true, Fields = [.. template.Fields] }
                        : await provisioner.EnsureAsync(template, ct);
                    state.Add(contentType);
                }
            }
            else
            {
                var name = reference.RequiredAttr("Name");
                contentType = state.ByName.GetValueOrDefault(name) ?? await db.ContentTypes.FirstOrDefaultAsync(c => c.Name == name, ct);
                if (contentType is null && name == ContentType.ItemName)
                {
                    contentType = await ItemContentTypeAsync(context, ct);
                }

                if (contentType is null)
                {
                    throw new TemplateException($"Content type '{name}' does not exist and is not part of the template.", reference);
                }
            }

            if (result.All(c => c.Id != contentType.Id))
            {
                result.Add(contentType);
            }
        }

        if (result.Count == 0)
        {
            result.Add(await ItemContentTypeAsync(context, ct));
        }

        return result;
    }

    private async Task<ContentType> ItemContentTypeAsync(TemplateContext context, CancellationToken ct)
    {
        if (await db.ContentTypes.FirstOrDefaultAsync(c => c.IsBuiltIn && c.Name == ContentType.ItemName, ct) is { } item)
        {
            return item;
        }

        if (context.DryRun)
        {
            return new ContentType { Id = Ids.New(), Name = ContentType.ItemName, IsBuiltIn = true };
        }

        var id = await ListEndpoints.EnsureItemContentTypeAsync(db, ct);
        return await db.ContentTypes.FirstAsync(c => c.Id == id, ct);
    }

    private static void CheckConflicts(IReadOnlyList<ContentType> contentTypes, string name, XElement element)
    {
        var fields = new List<FieldDefinition>();
        foreach (var contentType in contentTypes)
        {
            if (ListSchema.FindConflict(fields, contentType) is { } conflict)
            {
                throw new TemplateException($"List '{name}': {conflict}", element);
            }

            fields.AddRange(contentType.Fields);
        }
    }

    private async Task ApplyViewsAsync(XElement? section, ListSchema schema, string key, bool planned, TemplateContext context, CancellationToken ct)
    {
        if (section is null)
        {
            return;
        }

        var existing = planned ? [] : await db.Views.Where(v => v.ListId == schema.List.Id).ToListAsync(ct);
        var known = schema.Fields.Keys.Append("title").ToHashSet(StringComparer.Ordinal);
        foreach (var element in section.Elements(TemplateXml.Name("View")))
        {
            var name = element.RequiredAttr("Name").Trim();
            var columns = element.Elements(TemplateXml.Name("Column")).Select(c => c.Value.Trim()).ToList();
            var groupBy = element.Attr("GroupBy");
            var unknown = columns.Append(groupBy).OfType<string>().Where(c => !known.Contains(c)).ToList();
            if (unknown.Count > 0)
            {
                throw new TemplateException($"View '{name}' of list '{key}': unknown fields {string.Join(", ", unknown)}.", element);
            }

            if (runner.Validate(schema, element.Attr("Filter"), element.Attr("OrderBy")) is { } error)
            {
                throw new TemplateException($"View '{name}' of list '{key}': {error}", element);
            }

            var wanted = new ListView
            {
                Id = Ids.New(),
                ListId = schema.List.Id,
                Name = name,
                Columns = columns,
                Filter = element.Attr("Filter"),
                OrderBy = element.Attr("OrderBy"),
                GroupBy = groupBy,
                Layout = element.EnumAttr("Layout", ViewLayout.Table),
                IsDefault = element.BoolAttr("Default", false),
            };
            var view = existing.FirstOrDefault(v => v.Name == name);
            if (view is null)
            {
                context.Created(TemplateKinds.View, $"{key}: {name}");
                existing.Add(wanted);
                if (!context.DryRun)
                {
                    db.Views.Add(wanted);
                }

                view = wanted;
            }
            else if (!view.Columns.SequenceEqual(wanted.Columns, StringComparer.Ordinal) || view.Filter != wanted.Filter || view.OrderBy != wanted.OrderBy
                || view.GroupBy != wanted.GroupBy || view.Layout != wanted.Layout || view.IsDefault != wanted.IsDefault)
            {
                context.Updated(TemplateKinds.View, $"{key}: {name}");
                view.Columns = wanted.Columns;
                view.Filter = wanted.Filter;
                view.OrderBy = wanted.OrderBy;
                view.GroupBy = wanted.GroupBy;
                view.Layout = wanted.Layout;
                view.IsDefault = wanted.IsDefault;
            }

            if (view.IsDefault)
            {
                foreach (var other in existing.Where(v => v != view && v.IsDefault))
                {
                    other.IsDefault = false;
                }
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ApplyPermissionsAsync(XElement element, ListDefinition list, string key, bool planned, TemplateContext context, CancellationToken ct)
    {
        var unique = element.BoolAttr("Unique", false);
        var wanted = new List<PermissionGrant>();
        if (unique)
        {
            foreach (var grant in element.Elements(TemplateXml.Name("Grant")))
            {
                var level = grant.EnumAttr("Level", WorkspaceAccessLevel.Read);
                if (level == WorkspaceAccessLevel.None)
                {
                    throw new TemplateException("Grant: Level must be Read, Contribute or Manage.", grant);
                }

                var (type, id, label) = grant.Attr("Group") is { } group
                    ? (PrincipalType.Group, context.Resolve(TemplateKinds.Group, group) ?? await users.FindGroupAsync(group, ct), $"Group '{group}'")
                    : grant.Attr("User") is { } userName
                        ? (PrincipalType.User, await users.FindUserAsync(userName, ct), $"User '{userName}'")
                        : throw new TemplateException("Grant: a User or Group attribute is required.", grant);
                if (id is null)
                {
                    context.Warn($"{label} does not exist; not granted access to list '{key}'.", grant);
                    continue;
                }

                wanted.Add(new PermissionGrant { Id = Ids.New(), ListId = list.Id, ObjectId = list.Id, PrincipalType = type, PrincipalId = id.Value, Level = level });
            }

            wanted = [.. wanted.DistinctBy(g => (g.PrincipalType, g.PrincipalId))];
        }

        var existing = planned ? [] : await db.Grants.Where(g => g.ObjectId == list.Id).ToListAsync(ct);
        static string Describe(IEnumerable<PermissionGrant> grants) =>
            string.Join(";", grants.Select(g => $"{g.PrincipalType}:{g.PrincipalId}:{g.Level}").Order(StringComparer.Ordinal));
        if (list.HasUniquePermissions == unique && Describe(existing) == Describe(wanted))
        {
            return;
        }

        context.Updated(TemplateKinds.Permissions, key, unique ? $"unique, {wanted.Count} grants" : "inherited from the workspace");
        if (context.DryRun)
        {
            return;
        }

        db.Grants.RemoveRange(existing);
        db.Grants.AddRange(wanted);
        list.HasUniquePermissions = unique;
        await outbox.SaveChangesAsync(db, [ListIndexInvalidated.For(tenant, user, list.Id)], cancellationToken: ct);
    }
}
