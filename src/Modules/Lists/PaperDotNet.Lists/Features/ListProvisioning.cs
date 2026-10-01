using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Templates;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Content types of one template run (created, updated or planned), shared by the Lists sections.</summary>
internal sealed class ListsTemplateState
{
    private const string Key = "lists.contentTypes";

    public Dictionary<string, ContentType> ByName { get; } = [with(StringComparer.Ordinal)];

    public Dictionary<string, ContentType> ByKey { get; } = [with(StringComparer.Ordinal)];

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

/// <summary>Reference resolution shared by the Lists sections (templates use names, never ids). Queries name the tenant.</summary>
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
        return slash <= 0 ? null : await terms.FindTermSetAsync(context.TenantId, path[..slash], path[(slash + 1)..], ct);
    }

    /// <summary><c>Workspace/List</c> → list id: from this template run, else the tenant.</summary>
    public async Task<Guid?> ListAsync(string path, TemplateContext context, CancellationToken cancellationToken)
    {
        if (context.Resolve(TemplateKinds.List, path) is { } planned)
        {
            return planned;
        }

        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || await workspaces.FindSharedAsync(context.TenantId, path[..slash], cancellationToken) is not { } workspaceId)
        {
            return null;
        }

        var database = db;
        var tenant = context.TenantId;
        var workspace = workspaceId;
        var name = path[(slash + 1)..];
        var ct = cancellationToken;
        return await database.Lists.AsNoTracking()
            .Where(l => l.TenantId == tenant && l.WorkspaceId == workspace && l.Name == name && l.DeletedAt == null).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<string?> ListPathAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = tenantId;
        var id = listId;
        var ct = cancellationToken;
        var list = await database.Lists.AsNoTracking().Where(l => l.TenantId == tenant && l.Id == id).FirstOrDefaultAsync(ct);
        return list is null ? null
            : (await workspaces.GetNamesAsync(tenantId, [list.WorkspaceId], ct)).TryGetValue(list.WorkspaceId, out var workspace) ? $"{workspace}/{list.Name}" : null;
    }
}

/// <summary>Template values (<c>Library</c>, <c>Major</c>, <c>Board</c>, …) and the lower-case values the Lists tables store.</summary>
internal static class TemplateValues
{
    public static string ToXml(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    public static string FromXml(string value) => value.ToLowerInvariant();

    public static ContentTypeSchema Schema(ContentType contentType) => ContentTypeSchema.From(contentType);

    public static Task<ContentType?> ByNameAsync(ListsDbContext database, Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var wanted = name;
        var ct = cancellationToken;
        return db.ContentTypes.FirstOrDefaultAsync(c => c.TenantId == tenant && c.Name == wanted, ct);
    }

    public static Task<ContentType?> ByKeyAsync(ListsDbContext database, Guid tenantId, string key, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var wanted = key;
        var ct = cancellationToken;
        return db.ContentTypes.FirstOrDefaultAsync(c => c.TenantId == tenant && c.Key == wanted, ct);
    }

    public static Task<ContentType?> ByIdAsync(ListsDbContext database, Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var wanted = id;
        var ct = cancellationToken;
        return db.ContentTypes.FirstOrDefaultAsync(c => c.TenantId == tenant && c.Id == wanted, ct);
    }
}

/// <summary>
/// Template section <c>ContentTypes</c> (PRV-01/02). Custom content types carry their fields (term sets as <c>Group/Set</c>,
/// lookup lists as <c>Workspace/List</c>); built-in and extension ones are referenced by key and provisioned from their
/// templates. Fields are added or updated, never removed; a field's type, multiplicity and term set cannot change. Lookup
/// fields whose list comes later in the template are completed after the lists.
/// </summary>
internal sealed class ContentTypeTemplateHandler(
    ListsDbContext db,
    FieldTypeRegistry fieldTypes,
    IServiceProvider services,
    ListTemplateRegistry templates,
    ContentTypeProvisioner provisioner,
    ITermStore terms,
    ListTemplateLookups lookups) : ITemplateHandler
{
    private static readonly XName FieldName = TemplateXml.Name("Field");

    public XName Element => TemplateXml.Name("ContentTypes");

    public TemplateLevel Level => TemplateLevel.Tenant;

    public int Order => 400;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = context.TenantId;
        var ct = cancellationToken;
        var contentTypes = (await database.ContentTypes.AsNoTracking().Where(c => c.TenantId == tenant).OrderBy(c => c.Name).ToListAsync(ct))
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
            foreach (var field in ListsJsonText.Fields(contentType.Fields))
            {
                element.Add(await ExportFieldAsync(field, context, ct));
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
            termSet = await terms.GetTermSetPathAsync(context.TenantId, termSetId, ct);
            if (termSet is not null)
            {
                context.Require(TemplateKinds.TermSet, termSet);
            }
        }

        var lookup = field.LookupListId is { } listId ? await lookups.ListPathAsync(context.TenantId, listId, ct) : null;
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
            .With("Search", field.Search is { } weight ? weight.ToString() : null);
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

            var existing = await TemplateValues.ByNameAsync(db, context.TenantId, name, cancellationToken);
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
            var contentType = existing ?? new ContentType { Id = Ids.New(), TenantId = context.TenantId, Name = name };
            if (existing is null)
            {
                contentType.Description = element.Attr("Description");
                contentType.Fields = ListsJsonText.Fields(fields);
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
        var contentType = created && context.DryRun ? null : await TemplateValues.ByIdAsync(db, context.TenantId, contentTypeId, ct);
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
        var fields = ListsJsonText.Fields(contentType.Fields);
        var old = fields.FirstOrDefault(f => f.Name == field.Name);
        if (old is null)
        {
            contentType.Fields = ListsJsonText.Fields([.. fields, field]);
            context?.Updated(TemplateKinds.ContentType, contentType.Name, $"field added: {field.Name}");
            return;
        }

        if (old.Type != field.Type || old.AllowMultiple != field.AllowMultiple || old.TermSetId != field.TermSetId)
        {
            throw new TemplateException($"Content type '{contentType.Name}', field '{field.Name}': type, allowMultiple and term set cannot change once created.", element);
        }

        if (ListsJsonText.Fields([old]) != ListsJsonText.Fields([field]))
        {
            contentType.Fields = ListsJsonText.Fields([.. fields.Select(f => f.Name == field.Name ? field : f)]);
            context?.Updated(TemplateKinds.ContentType, contentType.Name, $"field changed: {field.Name}");
        }
    }

    private async Task<ContentType> ProvisionByKeyAsync(string key, XElement element, TemplateContext context, CancellationToken ct)
    {
        if (await TemplateValues.ByKeyAsync(db, context.TenantId, key, ct) is { } existing)
        {
            context.Register(TemplateKinds.ContentType, existing.Name, existing.Id);
            return existing;
        }

        var template = templates.FindContentType(key) ?? throw new TemplateException($"Content type key '{key}' is not known on this server.", element);
        if (template.ExtensionId is { } owner && context.Resolve(TemplateKinds.Extension, owner) is null
            && services.GetService<IExtensionAvailability>() is { } extensions && !await extensions.IsEnabledAsync(context.TenantId, owner, ct))
        {
            throw new TemplateException($"Content type '{key}' needs the extension '{owner}'; enable it or add it to the Extensions section.", element);
        }

        context.Created(TemplateKinds.ContentType, template.Name, $"from key {key}");
        var contentType = context.DryRun
            ? new ContentType { Id = Ids.New(), TenantId = context.TenantId, Name = template.Name, Key = key, IsBuiltIn = true, ExtensionId = template.ExtensionId, Fields = ListsJsonText.Fields(template.Fields) }
            : await provisioner.EnsureAsync(context.TenantId, template, ct);
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
        var copy = ListsJsonText.Fields(ListsJsonText.Fields([field]))[0];
        copy.LookupListId = Guid.Empty;
        return copy;
    }

    private async Task ValidateAsync(IReadOnlyList<FieldDefinition> fields, string name, XElement element, TemplateContext context, CancellationToken ct)
    {
        var errors = ContentTypeEndpoints.FieldErrors(fields, fieldTypes);
        if (services.GetService<IFieldTypeAvailability>() is { } availability)
        {
            foreach (var type in fields.Select(f => f.Type).Where(t => fieldTypes.Find(t) is not null).Distinct(StringComparer.Ordinal))
            {
                var enabledByTemplate = PossibleOwners(type).Any(owner => context.Resolve(TemplateKinds.Extension, owner) is not null);
                if (!enabledByTemplate && !await availability.IsAvailableAsync(context.TenantId, type, ct))
                {
                    errors.Add($"Field type '{type}' is not enabled for this organization.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new TemplateException($"Content type '{name}': {string.Join(" ", errors)}", element);
        }
    }
}
