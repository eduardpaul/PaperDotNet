using System.Collections.Frozen;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;

namespace PaperDotNet.Lists.Templates;

/// <summary>All content type and list templates (built-in, from apps on the SDK and from extensions), validated once.</summary>
internal sealed class ListTemplateRegistry
{
    private readonly FrozenDictionary<string, ContentTypeTemplate> _contentTypes;
    private readonly FrozenDictionary<string, ListTemplateDefinition> _lists;

    public ListTemplateRegistry(IEnumerable<ContentTypeTemplate> contentTypes, IEnumerable<ListTemplateDefinition> lists, FieldTypeRegistry fieldTypes)
    {
        var errors = new List<string>();
        var contentTypeList = contentTypes.ToList();
        var listList = lists.ToList();
        errors.AddRange(contentTypeList.GroupBy(c => c.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"Content type template '{g.Key}' is defined twice."));
        errors.AddRange(listList.GroupBy(l => l.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"List template '{g.Key}' is defined twice."));
        _contentTypes = contentTypeList.DistinctBy(c => c.Key).ToFrozenDictionary(c => c.Key, StringComparer.Ordinal);
        _lists = listList.DistinctBy(l => l.Key).ToFrozenDictionary(l => l.Key, StringComparer.Ordinal);

        foreach (var contentType in contentTypeList)
        {
            errors.AddRange(contentType.Fields.SelectMany(fieldTypes.Validate).Select(e => $"Content type template '{contentType.Key}': {e}"));
        }

        foreach (var list in listList)
        {
            if (list.ContentTypeKeys.Count == 0)
            {
                errors.Add($"List template '{list.Key}' needs a content type.");
            }

            var fields = new HashSet<string>(StringComparer.Ordinal) { "title" };
            foreach (var key in list.ContentTypeKeys)
            {
                if (_contentTypes.TryGetValue(key, out var contentType))
                {
                    fields.UnionWith(contentType.Fields.Select(f => f.Name));
                }
                else
                {
                    errors.Add($"List template '{list.Key}' uses unknown content type '{key}'.");
                }
            }

            foreach (var view in list.Views)
            {
                var unknown = view.Columns.Append(view.GroupBy).OfType<string>().Where(c => !fields.Contains(c)).ToList();
                if (unknown.Count > 0 || !ViewLayouts.IsValid(view.Layout))
                {
                    errors.Add($"List template '{list.Key}', view '{view.Name}': unknown fields ({string.Join(", ", unknown)}) or layout '{view.Layout}'.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid list templates: " + string.Join(" ", errors));
        }
    }

    public IEnumerable<ListTemplateDefinition> Lists => _lists.Values;

    public ListTemplateDefinition? FindList(string key) => _lists.GetValueOrDefault(key);

    public ContentTypeTemplate? FindContentType(string key) => _contentTypes.GetValueOrDefault(key);

    public IEnumerable<ContentTypeTemplate> ContentTypesOf(string extensionId) =>
        _contentTypes.Values.Where(c => c.ExtensionId == extensionId);
}

/// <summary>Creates template content types in a tenant (and keeps extension-managed ones in sync).</summary>
internal sealed class ContentTypeProvisioner(ListsDbContext db, ListTemplateRegistry registry) : IContentTypeProvisioning
{
    public async Task ProvisionExtensionAsync(Guid tenantId, string extensionId, CancellationToken cancellationToken)
    {
        foreach (var template in registry.ContentTypesOf(extensionId))
        {
            await EnsureAsync(tenantId, template, cancellationToken);
        }
    }

    /// <summary>
    /// The tenant's content type for <paramref name="template"/>, created when missing. Built-in ones are never
    /// overwritten (tenants may customize them); extension ones follow the extension.
    /// </summary>
    public async Task<ContentType> EnsureAsync(Guid tenantId, ContentTypeTemplate template, CancellationToken cancellationToken)
    {
        var fields = ListsJsonText.Fields(template.Fields);
        for (var attempt = 0; ; attempt++)
        {
            var existing = await FindByKeyAsync(tenantId, template.Key, cancellationToken);
            if (existing is not null)
            {
                if (template.ExtensionId is not null && existing.Fields != fields)
                {
                    existing.Fields = fields;
                    await db.SaveChangesAsync(cancellationToken);
                }

                return existing;
            }

            var name = template.Name;
            for (var n = 2; await NameTakenAsync(tenantId, name, cancellationToken); n++)
            {
                name = $"{template.Name} ({n})";
            }

            var contentType = new ContentType
            {
                Id = Ids.New(),
                TenantId = tenantId,
                Name = name,
                Description = template.Description,
                IsBuiltIn = true,
                Key = template.Key,
                ExtensionId = template.ExtensionId,
                Fields = fields,
            };
            db.ContentTypes.Add(contentType);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return contentType;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Provisioned concurrently: read it back.
                db.ChangeTracker.Clear();
            }
        }
    }

    private Task<ContentType?> FindByKeyAsync(Guid tenantId, string key, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var k = key;
        var ct = cancellationToken;
        return context.ContentTypes.Where(c => c.TenantId == tenant && c.Key == k).FirstOrDefaultAsync(ct);
    }

    private Task<bool> NameTakenAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var n = name;
        var ct = cancellationToken;
        return context.ContentTypes.AnyAsync(c => c.TenantId == tenant && c.Name == n, ct);
    }
}

/// <summary>Default when no extension runtime is present: extension templates are never available.</summary>
internal sealed class NoExtensions : IExtensionAvailability
{
    public ValueTask<bool> IsEnabledAsync(Guid tenantId, string extensionId, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

/// <summary>Built-in content types and list templates (LST-16); apps on the SDK and extensions add theirs the same way.</summary>
internal static class BuiltInTemplates
{
    internal const string DocumentKey = "document";
    internal const string DocumentsListKey = "documents";

    private static FieldDefinition Field(string name, string displayName, string type, FieldSearchWeight? search = null) =>
        new() { Name = name, DisplayName = displayName, Type = type, Search = search };

    // The document's keywords field (managed metadata) comes with Taxonomy (T12).
    public static readonly ContentTypeTemplate[] ContentTypes =
    [
        new(DocumentKey, "Document", "A document with a description.",
        [
            Field("description", "Description", "note"),
        ]),
        new("contact", "Contact", "A person or organization to contact.",
        [
            Field("email", "E-mail", "email", FieldSearchWeight.High),
            Field("phone", "Phone", "text"),
            Field("company", "Company", "text", FieldSearchWeight.High),
            Field("jobTitle", "Job title", "text"),
            Field("notes", "Notes", "note"),
        ]),
    ];

    public static readonly ListTemplateDefinition[] Lists =
    [
        new(DocumentsListKey, "Documents", "A document library with version history.", [DocumentKey],
        [
            new ViewTemplate("All documents", ["title", "description"], IsDefault: true),
        ])
        {
            IsLibrary = true,
            Versioning = true,
        },
        new("contacts", "Contacts", "People and organizations.", ["contact"],
        [
            new ViewTemplate("All contacts", ["title", "company", "email", "phone"], OrderBy: "fields/title", IsDefault: true),
        ]),
    ];
}

public sealed record ListTemplateContentType(string Key, string Name);

public sealed record ListTemplateResponse(
    string Key,
    string Name,
    string? Description,
    bool IsLibrary,
    bool Versioning,
    IReadOnlyList<ListTemplateContentType> ContentTypes,
    IReadOnlyList<string> Views,
    string? ExtensionId);

/// <summary>List templates available in the tenant (built-in and from enabled extensions, LST-16).</summary>
internal static class ListTemplateEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/v1.0/listTemplates", ListAsync).RequireScope(ListScopes.Read).WithTags("Lists").WithName("ListListTemplates");

    private static async Task<Ok<List<ListTemplateResponse>>> ListAsync(
        Caller caller, ListTemplateRegistry registry, IExtensionAvailability extensions, CancellationToken cancellationToken)
    {
        var result = new List<ListTemplateResponse>();
        foreach (var template in registry.Lists.OrderBy(t => t.ExtensionId is not null).ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            if (template.ExtensionId is { } owner && !await extensions.IsEnabledAsync(caller.TenantId, owner, cancellationToken))
            {
                continue;
            }

            result.Add(new ListTemplateResponse(
                template.Key,
                template.Name,
                template.Description,
                template.IsLibrary,
                template.Versioning,
                [.. template.ContentTypeKeys.Select(k => new ListTemplateContentType(k, registry.FindContentType(k)!.Name))],
                [.. template.Views.Select(v => v.Name)],
                template.ExtensionId));
        }

        return TypedResults.Ok(result);
    }

    /// <summary>The template of a new list, if it is available in the tenant.</summary>
    internal static async Task<ListTemplateDefinition?> FindAvailableAsync(
        ListTemplateRegistry registry, IExtensionAvailability extensions, Guid tenantId, string key, CancellationToken cancellationToken) =>
        registry.FindList(key) is { } template && (template.ExtensionId is not { } owner || await extensions.IsEnabledAsync(tenantId, owner, cancellationToken))
            ? template
            : null;

    /// <summary>The views of a template, for a new list.</summary>
    internal static IEnumerable<ListView> Views(ListDefinition list, ListTemplateDefinition template) => template.Views.Select(v => new ListView
    {
        Id = Ids.New(),
        TenantId = list.TenantId,
        ListId = list.Id,
        Name = v.Name,
        Columns = ListsJsonText.Strings(v.Columns),
        Filter = v.Filter,
        OrderBy = v.OrderBy,
        GroupBy = v.GroupBy,
        Layout = v.Layout,
        IsDefault = v.IsDefault,
    });
}
