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

/// <summary>Content types of one template run (created, updated or planned), shared by the Lists sections.</summary>
internal sealed class ListsTemplateState
{
    private const string Key = "lists.contentTypes";

    public Dictionary<string, ContentType> ByName { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, ContentType> ByKey { get; } = new(StringComparer.Ordinal);

    public static ListsTemplateState Of(TemplateContext context)
    {
        if (!context.Items.TryGetValue(Key, out var state))
        {
            state = new ListsTemplateState();
            context.Items[Key] = state;
        }

        return (ListsTemplateState)state;
    }

    public void Add(ContentType contentType)
    {
        ByName[contentType.Name] = contentType;
        if (contentType.Key is { } key)
        {
            ByKey[key] = contentType;
        }
    }
}

/// <summary>Reference resolution shared by the Lists sections (templates use names, never ids).</summary>
internal sealed class ListTemplateLookups(ListsDbContext db, IWorkspaceAccess workspaces, ITermStore terms)
{
    /// <summary><c>Group/Set</c> → term set id: from this template run, else the tenant.</summary>
    public async Task<Guid?> TermSetAsync(string path, TemplateContext context, CancellationToken ct)
    {
        if (context.Resolve(TemplateKinds.TermSet, path) is { } planned)
        {
            return planned;
        }

        var slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? null : await terms.FindTermSetAsync(path[..slash], path[(slash + 1)..], ct);
    }

    /// <summary><c>Workspace/List</c> → list id: from this template run, else the tenant.</summary>
    public async Task<Guid?> ListAsync(string path, TemplateContext context, CancellationToken ct)
    {
        if (context.Resolve(TemplateKinds.List, path) is { } planned)
        {
            return planned;
        }

        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || await workspaces.FindSharedAsync(path[..slash], ct) is not { } workspaceId)
        {
            return null;
        }

        var name = path[(slash + 1)..];
        return await db.Lists.Where(l => l.WorkspaceId == workspaceId && l.Name == name).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<string?> ListPathAsync(Guid listId, CancellationToken ct)
    {
        var list = await db.Lists.AsNoTracking().Where(l => l.Id == listId).Select(l => new { l.WorkspaceId, l.Name }).FirstOrDefaultAsync(ct);
        return list is null ? null
            : (await workspaces.GetNamesAsync([list.WorkspaceId], ct)).TryGetValue(list.WorkspaceId, out var workspace) ? $"{workspace}/{list.Name}" : null;
    }
}

/// <summary>
/// Template section <c>ContentTypes</c> (PRV-01/02). Custom content types carry their fields (term sets
/// as <c>Group/Set</c>, lookup lists as <c>Workspace/List</c>); built-in and extension ones are
/// referenced by key and provisioned from their templates. Fields are added or updated, never removed;
/// a field's type, multiplicity and term set cannot change. Lookup fields whose list comes later in the
/// template are completed after the lists.
/// </summary>
internal sealed class ContentTypeTemplateHandler(
    ListsDbContext db,
    FieldTypeRegistry fieldTypes,
    IFieldTypeAvailability availability,
    ListTemplateRegistry templates,
    ContentTypeProvisioner provisioner,
    IExtensionAvailability extensions,
    ITermStore terms,
    ListTemplateLookups lookups) : ITemplateHandler
{
    private static readonly XName FieldName = TemplateXml.Name("Field");

    public XName Element => TemplateXml.Name("ContentTypes");

    public TemplateLevel Level => TemplateLevel.Tenant;

    public int Order => 400;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var contentTypes = (await db.ContentTypes.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken))
            .Where(c => !(c.IsBuiltIn && c.Key is null) && context.Includes(TemplateKinds.ContentType, c.Name))
            .ToList();
        if (contentTypes.Count == 0)
        {
            return null;
        }

        var section = new XElement(Element);
        foreach (var contentType in contentTypes)
        {
            var element = new XElement(TemplateXml.Name("ContentType")).With("Name", contentType.Name);
            if (contentType.Key is not null)
            {
                element.With("Key", contentType.Key);
                if (contentType.ExtensionId is { } extensionId)
                {
                    context.Require(TemplateKinds.Extension, extensionId);
                }

                section.Add(element);
                continue;
            }

            element.With("Description", contentType.Description);
            foreach (var field in contentType.Fields)
            {
                element.Add(await ExportFieldAsync(field, context, cancellationToken));
            }

            section.Add(element);
        }

        return section;
    }

    private async Task<XElement> ExportFieldAsync(FieldDefinition field, TemplateContext context, CancellationToken ct)
    {
        string? termSet = null;
        if (field.TermSetId is { } termSetId)
        {
            termSet = await terms.GetTermSetPathAsync(termSetId, ct);
            if (termSet is not null)
            {
                context.Require(TemplateKinds.TermSet, termSet);
            }
        }

        var lookup = field.LookupListId is { } listId ? await lookups.ListPathAsync(listId, ct) : null;
        foreach (var owner in PossibleOwners(field.Type))
        {
            context.Require(TemplateKinds.Extension, owner);
        }

        return new XElement(FieldName, field.Choices.Select(c => new XElement(TemplateXml.Name("Choice"), c)))
            .With("Name", field.Name)
            .With("DisplayName", field.DisplayName == field.Name ? null : field.DisplayName)
            .With("Type", field.Type)
            .With("Description", field.Description)
            .With("Required", field.Required, omitDefault: true)
            .With("AllowMultiple", field.AllowMultiple, omitDefault: true)
            .With("MaxLength", field.MaxLength)
            .With("Minimum", field.Minimum)
            .With("Maximum", field.Maximum)
            .With("CurrencyCode", field.CurrencyCode)
            .With("TermSet", termSet)
            .With("LookupList", lookup)
            .With("DefaultValue", field.DefaultValue)
            .With("Search", field.Search);
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var state = ListsTemplateState.Of(context);
        foreach (var element in section.Elements(TemplateXml.Name("ContentType")))
        {
            var name = element.RequiredAttr("Name").Trim();
            if (element.Attr("Key") is { } key)
            {
                state.Add(await ProvisionByKeyAsync(key, element, context, cancellationToken));
                continue;
            }

            var existing = await db.ContentTypes.FirstOrDefaultAsync(c => c.Name == name, cancellationToken);
            if (existing is { Key: not null } or { ExtensionId: not null })
            {
                context.Warn($"Content type '{name}' is built in or managed by an extension; its fields were not changed.", element);
                state.Add(existing);
                continue;
            }

            var fields = new List<FieldDefinition>();
            var pendingLookups = new List<(FieldDefinition Field, string Path, XElement Element)>();
            foreach (var fieldElement in element.Elements(FieldName))
            {
                var (field, lookupPath) = await ReadFieldAsync(fieldElement, context, cancellationToken);
                if (lookupPath is not null)
                {
                    pendingLookups.Add((field, lookupPath, fieldElement));
                }
                else
                {
                    fields.Add(field);
                }
            }

            await ValidateAsync([.. fields, .. pendingLookups.Select(p => WithPlaceholderLookup(p.Field))], name, element, context, cancellationToken);
            var contentType = existing ?? new ContentType { Id = Ids.New(), Name = name };
            if (existing is null)
            {
                contentType.Description = element.Attr("Description");
                contentType.Fields = fields;
                context.Created(TemplateKinds.ContentType, name);
                if (!context.DryRun)
                {
                    db.ContentTypes.Add(contentType);
                }
            }
            else
            {
                if (contentType.Description != element.Attr("Description"))
                {
                    context.Updated(TemplateKinds.ContentType, name, "description");
                    contentType.Description = element.Attr("Description");
                }

                foreach (var field in fields)
                {
                    Merge(contentType, field, context, element);
                }
            }

            if (!context.DryRun)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            state.Add(contentType);
            context.Register(TemplateKinds.ContentType, name, contentType.Id);
            if (pendingLookups.Count > 0)
            {
                var created = existing is null;
                context.Defer(ct => CompleteLookupsAsync(contentType.Id, created, pendingLookups, context, ct));
            }
        }
    }

    /// <summary>Adds or updates lookup fields once the lists of the template exist.</summary>
    /// <remarks>Part of creating a new content type (no change of its own); a field added to an existing one is an update.</remarks>
    private async Task CompleteLookupsAsync(Guid contentTypeId, bool created, List<(FieldDefinition Field, string Path, XElement Element)> pending, TemplateContext context, CancellationToken ct)
    {
        var contentType = created && context.DryRun ? null : await db.ContentTypes.FirstOrDefaultAsync(c => c.Id == contentTypeId, ct);
        foreach (var (field, path, element) in pending)
        {
            field.LookupListId = await lookups.ListAsync(path, context, ct)
                ?? throw new TemplateException($"Field '{field.Name}': the lookup list '{path}' does not exist and is not part of the template.", element);
            if (contentType is not null)
            {
                Merge(contentType, field, created ? null : context, element);
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static void Merge(ContentType contentType, FieldDefinition field, TemplateContext? context, XElement element)
    {
        var old = contentType.Fields.FirstOrDefault(f => f.Name == field.Name);
        if (old is null)
        {
            contentType.Fields = [.. contentType.Fields, field];
            context?.Updated(TemplateKinds.ContentType, contentType.Name, $"field added: {field.Name}");
            return;
        }

        if (old.Type != field.Type || old.AllowMultiple != field.AllowMultiple || old.TermSetId != field.TermSetId)
        {
            throw new TemplateException($"Content type '{contentType.Name}', field '{field.Name}': type, allowMultiple and term set cannot change once created.", element);
        }

        if (JsonSerializer.Serialize(old) != JsonSerializer.Serialize(field))
        {
            contentType.Fields = [.. contentType.Fields.Select(f => f.Name == field.Name ? field : f)];
            context?.Updated(TemplateKinds.ContentType, contentType.Name, $"field changed: {field.Name}");
        }
    }

    private async Task<ContentType> ProvisionByKeyAsync(string key, XElement element, TemplateContext context, CancellationToken ct)
    {
        if (await db.ContentTypes.FirstOrDefaultAsync(c => c.Key == key, ct) is { } existing)
        {
            context.Register(TemplateKinds.ContentType, existing.Name, existing.Id);
            return existing;
        }

        var template = templates.FindContentType(key) ?? throw new TemplateException($"Content type key '{key}' is not known on this server.", element);
        if (template.ExtensionId is { } owner && context.Resolve(TemplateKinds.Extension, owner) is null && !await extensions.IsEnabledAsync(owner, ct))
        {
            throw new TemplateException($"Content type '{key}' needs the extension '{owner}'; enable it or add it to the Extensions section.", element);
        }

        context.Created(TemplateKinds.ContentType, template.Name, $"from key {key}");
        var contentType = context.DryRun
            ? new ContentType { Id = Ids.New(), Name = template.Name, Key = key, IsBuiltIn = true, ExtensionId = template.ExtensionId, Fields = [.. template.Fields] }
            : await provisioner.EnsureAsync(template, ct);
        context.Register(TemplateKinds.ContentType, contentType.Name, contentType.Id);
        return contentType;
    }

    private async Task<(FieldDefinition Field, string? LookupPath)> ReadFieldAsync(XElement element, TemplateContext context, CancellationToken ct)
    {
        var name = element.RequiredAttr("Name").Trim();
        var field = new FieldDefinition
        {
            Name = name,
            DisplayName = element.Attr("DisplayName")?.Trim() is { Length: > 0 } display ? display : name,
            Type = element.RequiredAttr("Type"),
            Description = element.Attr("Description"),
            Required = element.BoolAttr("Required", false),
            AllowMultiple = element.BoolAttr("AllowMultiple", false),
            MaxLength = element.IntAttr("MaxLength"),
            Minimum = element.DecimalAttr("Minimum"),
            Maximum = element.DecimalAttr("Maximum"),
            Choices = [.. element.Elements(TemplateXml.Name("Choice")).Select(c => c.Value)],
            CurrencyCode = element.Attr("CurrencyCode"),
            DefaultValue = element.Attr("DefaultValue"),
            Search = element.Attr("Search") is null ? null : element.EnumAttr("Search", FieldSearchWeight.Normal),
        };
        if (element.Attr("TermSet") is { } termSet)
        {
            field.TermSetId = await lookups.TermSetAsync(termSet, context, ct)
                ?? throw new TemplateException($"Field '{name}': the term set '{termSet}' does not exist and is not part of the template.", element);
        }

        if (element.Attr("LookupList") is { } lookupPath)
        {
            if (await lookups.ListAsync(lookupPath, context, ct) is { } listId)
            {
                field.LookupListId = listId;
                return (field, null);
            }

            return (field, lookupPath);
        }

        return (field, null);
    }

    /// <summary>Extension field types are named <c>{extension id}.{name}</c>; ids contain dots, so every prefix is a candidate.</summary>
    private static IEnumerable<string> PossibleOwners(string fieldType)
    {
        for (var dot = fieldType.IndexOf('.', StringComparison.Ordinal); dot > 0; dot = fieldType.IndexOf('.', dot + 1))
        {
            yield return fieldType[..dot];
        }
    }

    private static FieldDefinition WithPlaceholderLookup(FieldDefinition field)
    {
        var copy = JsonSerializer.Deserialize<FieldDefinition>(JsonSerializer.Serialize(field))!;
        copy.LookupListId = Guid.Empty;
        return copy;
    }

    private async Task ValidateAsync(IReadOnlyList<FieldDefinition> fields, string name, XElement element, TemplateContext context, CancellationToken ct)
    {
        var errors = ContentTypeEndpoints.FieldErrors(fields, fieldTypes);
        foreach (var type in fields.Select(f => f.Type).Where(t => fieldTypes.Find(t) is not null).Distinct(StringComparer.Ordinal))
        {
            var enabledByTemplate = PossibleOwners(type).Any(owner => context.Resolve(TemplateKinds.Extension, owner) is not null);
            if (!enabledByTemplate && !await availability.IsAvailableAsync(type, ct))
            {
                errors.Add($"Field type '{type}' is not enabled for this organization.");
            }
        }

        if (errors.Count > 0)
        {
            throw new TemplateException($"Content type '{name}': {string.Join(" ", errors)}", element);
        }
    }
}

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
