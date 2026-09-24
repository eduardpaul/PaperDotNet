using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;

namespace PaperDotNet.Lists.Templates;

/// <summary>All content type and list templates (built-in and from extensions), validated once.</summary>
internal sealed class ListTemplateRegistry
{
    private static readonly HashSet<string> Layouts = ["table", "board", "calendar", "gallery"];

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
                if (unknown.Count > 0 || !Layouts.Contains(view.Layout))
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

/// <summary>Creates template content types in the current tenant (and keeps extension-managed ones in sync).</summary>
internal sealed class ContentTypeProvisioner(ListsDbContext db, ListTemplateRegistry registry) : IContentTypeProvisioning
{
    public async Task ProvisionExtensionAsync(string extensionId, CancellationToken cancellationToken)
    {
        foreach (var template in registry.ContentTypesOf(extensionId))
        {
            await EnsureAsync(template, cancellationToken);
        }
    }

    /// <summary>
    /// The tenant's content type for <paramref name="template"/>, created when missing. Built-in
    /// ones are never overwritten (tenants may customize them); extension ones follow the extension.
    /// </summary>
    public async Task<ContentType> EnsureAsync(ContentTypeTemplate template, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.ContentTypes.FirstOrDefaultAsync(c => c.Key == template.Key, ct);
            if (existing is not null)
            {
                if (template.ExtensionId is not null && JsonSerializer.Serialize(existing.Fields) != JsonSerializer.Serialize(template.Fields))
                {
                    existing.Fields = Clone(template.Fields);
                    await db.SaveChangesAsync(ct);
                }

                return existing;
            }

            var name = template.Name;
            for (var n = 2; await db.ContentTypes.AnyAsync(c => c.Name == name, ct); n++)
            {
                name = $"{template.Name} ({n})";
            }

            var contentType = new ContentType
            {
                Id = Ids.New(),
                Name = name,
                Description = template.Description,
                IsBuiltIn = true,
                Key = template.Key,
                ExtensionId = template.ExtensionId,
                Fields = Clone(template.Fields),
            };
            db.ContentTypes.Add(contentType);
            try
            {
                await db.SaveChangesAsync(ct);
                return contentType;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Provisioned concurrently: read it back.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Templates are shared singletons: every entity gets its own copies.</summary>
    private static List<FieldDefinition> Clone(IReadOnlyList<FieldDefinition> fields) =>
        JsonSerializer.Deserialize<List<FieldDefinition>>(JsonSerializer.Serialize(fields))!;
}

/// <summary>Default when no extension runtime is present: extension templates are never available.</summary>
internal sealed class NoExtensions : IExtensionAvailability
{
    public ValueTask<bool> IsEnabledAsync(string extensionId, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
