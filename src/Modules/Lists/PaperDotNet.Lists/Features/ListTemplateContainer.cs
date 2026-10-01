using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Lists.Templates;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// The <c>List</c> element of templates (PRV-01/02): settings, content types (by name or key), views and list
/// permissions. Matched by name in the workspace; system lists are never part of templates. Content types and views are
/// added or updated, never removed; when <c>Permissions</c> is present the list's inheritance and grants are made to match it.
/// </summary>
internal sealed class ListTemplateContainer(
    ListsDbContext db,
    ListTemplateRegistry templates,
    ContentTypeProvisioner provisioner,
    IServiceProvider services,
    ItemQueryRunner runner,
    IUserDirectory users) : ITemplateContainer
{
    private static readonly Dictionary<string, string> RoleTypes = new(StringComparer.Ordinal)
    {
        ["Visitors"] = AclPrincipalTypes.WorkspaceVisitors,
        ["Members"] = AclPrincipalTypes.WorkspaceMembers,
        ["Owners"] = AclPrincipalTypes.WorkspaceOwners,
    };

    public TemplateLevel Level => TemplateLevel.List;

    public async Task<IReadOnlyList<Guid>> ListAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = context.TenantId;
        var workspace = context.WorkspaceId!.Value;
        var ct = cancellationToken;
        return await database.Lists.AsNoTracking()
            .Where(l => l.TenantId == tenant && l.WorkspaceId == workspace && l.SystemKey == null && l.DeletedAt == null)
            .OrderBy(l => l.Name).Select(l => l.Id).ToListAsync(ct);
    }

    public async Task<XElement> ExportAsync(Guid id, TemplateContext context, CancellationToken cancellationToken)
    {
        var list = await FindAsync(context.TenantId, id, tracking: false, cancellationToken) ?? throw new TemplateException("The list was not found.");
        context.ListName = list.Name;
        var refs = new XElement(TemplateXml.Name("ContentTypes"));
        foreach (var contentTypeId in ListsJsonText.Ids(list.ContentTypeIds))
        {
            if (await TemplateValues.ByIdAsync(db, context.TenantId, contentTypeId, cancellationToken) is not { } contentType)
            {
                continue;
            }

            context.Require(TemplateKinds.ContentType, contentType.Name);
            refs.Add(contentType.Key is { } key
                ? new XElement(TemplateXml.Name("ContentTypeRef"), new XAttribute("Key", key))
                : new XElement(TemplateXml.Name("ContentTypeRef"), new XAttribute("Name", contentType.Name)));
        }

        var views = await ViewsAsync(context.TenantId, id, tracking: false, cancellationToken);
        return new XElement(TemplateXml.Name("List"),
                refs,
                views.Count == 0 ? null : new XElement(TemplateXml.Name("Views"), views.OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => new XElement(TemplateXml.Name("View"),
                        ListsJsonText.Strings(v.Columns).Select(c => new XElement(TemplateXml.Name("Column"), c)))
                    .With("Name", v.Name).With("Layout", TemplateValues.ToXml(v.Layout)).With("Default", v.IsDefault, omitDefault: true)
                    .With("Filter", v.Filter).With("OrderBy", v.OrderBy).With("GroupBy", v.GroupBy))),
                await ExportPermissionsAsync(list, context, cancellationToken))
            .With("Name", list.Name)
            .With("Description", list.Description)
            .With("Kind", list.Kind == ListKinds.Library ? TemplateValues.ToXml(list.Kind) : null)
            .With("AllowFolders", list.AllowFolders ? null : false)
            .With("Versioning", TemplateValues.ToXml(list.Versioning))
            .With("MaxVersions", list.MaxVersions == ListDefinition.DefaultMaxVersions ? null : list.MaxVersions)
            .With("Template", list.TemplateKey);
    }

    private async Task<XElement> ExportPermissionsAsync(ListDefinition list, TemplateContext context, CancellationToken ct)
    {
        var element = new XElement(TemplateXml.Name("Permissions")).With("Unique", list.HasUniquePermissions);
        if (!list.HasUniquePermissions)
        {
            return element;
        }

        // Workspace roles are exported by name (they apply to whichever workspace imports the list); owners are implied.
        var entries = (await EntriesAsync(context.TenantId, list.Id, tracking: false, ct)).Where(e => e.PrincipalType != AclPrincipalTypes.WorkspaceOwners).ToList();
        var userNames = await users.GetUserNamesAsync(context.TenantId, [.. entries.Where(e => e.PrincipalType == AclPrincipalTypes.User).Select(e => e.PrincipalId)], ct);
        var groupNames = await users.GetGroupNamesAsync(context.TenantId, [.. entries.Where(e => e.PrincipalType == AclPrincipalTypes.Group).Select(e => e.PrincipalId)], ct);
        foreach (var entry in entries)
        {
            var (attribute, name) = entry.PrincipalType switch
            {
                AclPrincipalTypes.User => ("User", userNames.GetValueOrDefault(entry.PrincipalId)),
                AclPrincipalTypes.Group => ("Group", groupNames.GetValueOrDefault(entry.PrincipalId)),
                var type => ("Role", RoleTypes.FirstOrDefault(r => r.Value == type).Key),
            };
            if (name is null)
            {
                continue;
            }

            if (entry.PrincipalType == AclPrincipalTypes.Group)
            {
                context.Require(TemplateKinds.Group, name);
            }

            element.Add(new XElement(TemplateXml.Name("Grant"), new XAttribute(attribute, name), new XAttribute("Level", ((WorkspaceAccessLevel)entry.Level).ToString())));
        }

        element.ReplaceNodes(element.Elements().OrderBy(e => e.ToString(), StringComparer.Ordinal).ToList());
        return element;
    }

    public async Task ApplyAsync(XElement element, TemplateContext context, CancellationToken cancellationToken)
    {
        var name = element.RequiredAttr("Name").Trim();
        var key = $"{context.WorkspaceName}/{name}";
        var contentTypes = await ContentTypesAsync(element, context, cancellationToken);
        var list = context.IsPlanned ? null : await FindByNameAsync(context.TenantId, context.WorkspaceId!.Value, name, cancellationToken);
        if (list is { SystemKey: not null })
        {
            throw new TemplateException($"List '{name}' is a system list and cannot be provisioned.", element);
        }

        var kind = TemplateValues.FromXml(element.Attr("Kind") ?? "List");
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
                TenantId = context.TenantId,
                WorkspaceId = context.WorkspaceId!.Value,
                Name = name,
                Description = element.Attr("Description"),
                Kind = kind,
                AllowFolders = element.BoolAttr("AllowFolders", true),
                ContentTypeIds = ListsJsonText.Ids(contentTypes.Select(c => c.Id)),
                Versioning = element.Attr("Versioning") is { } versioning ? TemplateValues.FromXml(versioning) : kind == ListKinds.Library ? ListVersionings.Major : ListVersionings.Off,
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
            if (element.Attr("Versioning") is { } versioning)
            {
                Change("versioning", list.Versioning, TemplateValues.FromXml(versioning), v => list.Versioning = v);
            }

            if (element.IntAttr("MaxVersions") is { } maxVersions)
            {
                Change("maxVersions", list.MaxVersions, maxVersions, v => list.MaxVersions = v);
            }

            var ids = ListsJsonText.Ids(list.ContentTypeIds);
            var current = new List<ContentType>();
            foreach (var id in ids)
            {
                if (await TemplateValues.ByIdAsync(db, context.TenantId, id, cancellationToken) is { } existingType)
                {
                    current.Add(existingType);
                }
            }

            foreach (var contentType in contentTypes.Where(c => !ids.Contains(c.Id)))
            {
                if (ListSchema.FindConflict(current.SelectMany(c => ListsJsonText.Fields(c.Fields)), ListsJsonText.Fields(contentType.Fields)) is { } conflict)
                {
                    throw new TemplateException($"List '{key}': {conflict}", element);
                }

                current.Add(contentType);
                ids.Add(contentType.Id);
                changes.Add($"content type added: {contentType.Name}");
            }

            list.ContentTypeIds = ListsJsonText.Ids(ids);
            if (changes.Count > 0)
            {
                context.Updated(TemplateKinds.List, key, string.Join(", ", changes));
            }

            contentTypes = current;
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        context.ListId = list.Id;
        context.ListName = name;
        context.IsPlanned = planned;
        context.Register(TemplateKinds.List, key, list.Id);

        var schema = new ListSchema(list, [.. contentTypes.Select(ContentTypeSchema.From)], ListAccess.Full(list.Id));
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
                contentType = state.ByKey.GetValueOrDefault(key) ?? await TemplateValues.ByKeyAsync(db, context.TenantId, key, ct);
                if (contentType is null)
                {
                    var template = templates.FindContentType(key) ?? throw new TemplateException($"Content type key '{key}' is not known on this server.", reference);
                    if (template.ExtensionId is { } owner && context.Resolve(TemplateKinds.Extension, owner) is null
                        && services.GetService<IExtensionAvailability>() is { } extensions && !await extensions.IsEnabledAsync(context.TenantId, owner, ct))
                    {
                        throw new TemplateException($"Content type '{key}' needs the extension '{owner}'.", reference);
                    }

                    contentType = context.DryRun
                        ? new ContentType { Id = Ids.New(), TenantId = context.TenantId, Name = template.Name, Key = key, IsBuiltIn = true, Fields = ListsJsonText.Fields(template.Fields) }
                        : await provisioner.EnsureAsync(context.TenantId, template, ct);
                    state.Add(contentType);
                }
            }
            else
            {
                var name = reference.RequiredAttr("Name");
                contentType = state.ByName.GetValueOrDefault(name) ?? await TemplateValues.ByNameAsync(db, context.TenantId, name, ct);
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

    private async Task<ContentType> ItemContentTypeAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = context.TenantId;
        var itemName = ContentType.ItemName;
        var ct = cancellationToken;
        if (await database.ContentTypes.FirstOrDefaultAsync(c => c.TenantId == tenant && c.IsBuiltIn && c.Name == itemName, ct) is { } item)
        {
            return item;
        }

        if (context.DryRun)
        {
            return new ContentType { Id = Ids.New(), TenantId = context.TenantId, Name = ContentType.ItemName, IsBuiltIn = true };
        }

        var id = await ContentTypeEndpoints.EnsureItemContentTypeAsync(db, context.TenantId, ct);
        return (await TemplateValues.ByIdAsync(db, context.TenantId, id, ct))!;
    }

    private static void CheckConflicts(IReadOnlyList<ContentType> contentTypes, string name, XElement element)
    {
        var fields = new List<Contracts.FieldDefinition>();
        foreach (var contentType in contentTypes)
        {
            var own = ListsJsonText.Fields(contentType.Fields);
            if (ListSchema.FindConflict(fields, own) is { } conflict)
            {
                throw new TemplateException($"List '{name}': {conflict}", element);
            }

            fields.AddRange(own);
        }
    }

    private async Task ApplyViewsAsync(XElement? section, ListSchema schema, string key, bool planned, TemplateContext context, CancellationToken ct)
    {
        if (section is null)
        {
            return;
        }

        var existing = planned ? [] : await ViewsAsync(context.TenantId, schema.List.Id, tracking: true, ct);
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

            if (runner.Validate(schema, element.Attr("Filter"), element.Attr("OrderBy"), context.Actor.UserId) is { } error)
            {
                throw new TemplateException($"View '{name}' of list '{key}': {error}", element);
            }

            var wanted = new ListView
            {
                Id = Ids.New(),
                TenantId = context.TenantId,
                ListId = schema.List.Id,
                Name = name,
                Columns = ListsJsonText.Strings(columns),
                Filter = element.Attr("Filter"),
                OrderBy = element.Attr("OrderBy"),
                GroupBy = groupBy,
                Layout = TemplateValues.FromXml(element.Attr("Layout") ?? "Table"),
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
            else if (view.Columns != wanted.Columns || view.Filter != wanted.Filter || view.OrderBy != wanted.OrderBy
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
        var grants = new List<PermissionGrantDto>();
        if (unique)
        {
            foreach (var grant in element.Elements(TemplateXml.Name("Grant")))
            {
                var level = grant.EnumAttr("Level", WorkspaceAccessLevel.Read);
                if (level == WorkspaceAccessLevel.None)
                {
                    throw new TemplateException("Grant: Level must be Read, Contribute or Manage.", grant);
                }

                if (grant.Attr("Role") is { } role)
                {
                    grants.Add(RoleTypes.TryGetValue(role, out var roleType)
                        ? new PermissionGrantDto(roleType, list.WorkspaceId, level)
                        : throw new TemplateException("Grant: Role must be Visitors, Members or Owners.", grant));
                    continue;
                }

                var (type, id, label) = grant.Attr("Group") is { } group
                    ? (AclPrincipalTypes.Group, context.Resolve(TemplateKinds.Group, group) ?? await users.FindGroupAsync(context.TenantId, group, ct), $"Group '{group}'")
                    : grant.Attr("User") is { } userName
                        ? (AclPrincipalTypes.User, await users.FindUserAsync(context.TenantId, userName, ct), $"User '{userName}'")
                        : throw new TemplateException("Grant: a User, Group or Role attribute is required.", grant);
                if (id is null)
                {
                    context.Warn($"{label} does not exist; not granted access to list '{key}'.", grant);
                    continue;
                }

                grants.Add(new PermissionGrantDto(type, id.Value, level));
            }
        }

        var wanted = unique ? Acl.FromGrants(list, list.Id, grants) : [.. Acl.RoleEntries(list)];
        var existing = planned ? [.. Acl.RoleEntries(list)] : await EntriesAsync(context.TenantId, list.Id, tracking: true, ct);
        static string Describe(IEnumerable<AclEntry> entries) =>
            string.Join(";", entries.Select(e => $"{e.PrincipalId}:{e.Level}").Order(StringComparer.Ordinal));
        if (list.HasUniquePermissions == unique && Describe(existing) == Describe(wanted))
        {
            return;
        }

        context.Updated(TemplateKinds.Permissions, key, unique ? $"unique, {wanted.Count} entries" : "inherited from the workspace");
        if (context.DryRun)
        {
            return;
        }

        Acl.Replace(db, existing, wanted);
        list.HasUniquePermissions = unique;
        await db.SaveChangesAsync(ct);
    }

    private Task<ListDefinition?> FindAsync(Guid tenantId, Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = tenantId;
        var listId = id;
        var ct = cancellationToken;
        return tracking
            ? database.Lists.FirstOrDefaultAsync(l => l.TenantId == tenant && l.Id == listId && l.DeletedAt == null, ct)
            : database.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == tenant && l.Id == listId && l.DeletedAt == null, ct);
    }

    private Task<ListDefinition?> FindByNameAsync(Guid tenantId, Guid workspaceId, string name, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = tenantId;
        var workspace = workspaceId;
        var wanted = name;
        var ct = cancellationToken;
        return database.Lists.FirstOrDefaultAsync(l => l.TenantId == tenant && l.WorkspaceId == workspace && l.Name == wanted && l.DeletedAt == null, ct);
    }

    private async Task<List<ListView>> ViewsAsync(Guid tenantId, Guid listId, bool tracking, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = tenantId;
        var list = listId;
        var ct = cancellationToken;
        return tracking
            ? await database.Views.Where(v => v.TenantId == tenant && v.ListId == list).ToListAsync(ct)
            : await database.Views.AsNoTracking().Where(v => v.TenantId == tenant && v.ListId == list).ToListAsync(ct);
    }

    private async Task<List<AclEntry>> EntriesAsync(Guid tenantId, Guid scopeId, bool tracking, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = tenantId;
        var scope = scopeId;
        var ct = cancellationToken;
        return tracking
            ? await database.AclEntries.Where(e => e.TenantId == tenant && e.ScopeId == scope).ToListAsync(ct)
            : await database.AclEntries.AsNoTracking().Where(e => e.TenantId == tenant && e.ScopeId == scope).ToListAsync(ct);
    }
}
