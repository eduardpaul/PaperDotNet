using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// List section <c>Items</c> (PRV-04): the list's items and folders, in a JSON document of the template package. Values
/// are portable: terms as <c>Group/Set/Term</c> paths, keywords as text, people as user names, lookups as
/// <c>{ "list": "Workspace/List", "key": … }</c>. Apply creates the items that are missing, each with an id derived from
/// the list and its key (applying again creates nothing twice; existing items are never changed), through the normal
/// write path (validation, mutators, events); lookups are set once every list of the template is filled. Entries also
/// carry when and by whom items were created and changed (<c>created</c>, <c>createdBy</c>, <c>modified</c>,
/// <c>modifiedBy</c>, kept on import) and unique permissions (<c>permissions</c>: user or group names with a level).
/// </summary>
internal sealed class ListItemsTemplateHandler(
    ListsDbContext db, ListSchemaLoader loader, ItemWriter writer, ListTemplateLookups lookups, ITermStore terms, IUserDirectory users,
    AuditOverrides stamps) : ITemplateHandler
{
    public const string Kind = "items";

    public XName Element => TemplateXml.Name("Items");

    public TemplateLevel Level => TemplateLevel.List;

    public int Order => 800;

    // ---- Export ---------------------------------------------------------------

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        if (context.Package is not { } package)
        {
            return null;
        }

        var listId = context.ListId!.Value;
        var list = await db.Lists.AsNoTracking().FirstAsync(l => l.Id == listId, cancellationToken);
        var schema = (await loader.LoadAsSystemAsync(list.WorkspaceId, listId, cancellationToken))!;
        var items = await db.Items.AsNoTracking().Where(i => i.ListId == listId).ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return null;
        }

        var ordered = ParentsFirst(items);
        var values = ordered.ToDictionary(i => i.Id, i => JsonNode.Parse(i.Fields)!.AsObject());
        var fields = schema.ContentTypes.ToDictionary(c => c.Id, c => (IReadOnlyList<FieldDefinition>)c.Fields);
        var converter = await ExportConverter.CreateAsync(ordered, values, fields, terms, users, lookups, cancellationToken);
        var unique = ordered.Where(i => i.HasUniquePermissions).Select(i => i.Id).ToList();
        var grants = unique.Count == 0 ? [] : await db.Grants.AsNoTracking().Where(g => unique.Contains(g.ObjectId)).ToListAsync(cancellationToken);
        var people = await users.GetUserNamesAsync(
            [.. ordered.SelectMany(i => new[] { i.CreatedBy, i.UpdatedBy }).OfType<Guid>().Concat(grants.Where(g => g.PrincipalType == PrincipalType.User).Select(g => g.PrincipalId)).Distinct()],
            cancellationToken);
        var groups = await users.GetGroupNamesAsync([.. grants.Where(g => g.PrincipalType == PrincipalType.Group).Select(g => g.PrincipalId).Distinct()], cancellationToken);
        var exported = new JsonArray();
        foreach (var item in ordered)
        {
            var contentType = schema.FindContentType(item.ContentTypeId);
            var entry = new JsonObject
            {
                ["key"] = Key(item.Id),
                ["title"] = item.Title,
                ["contentType"] = contentType?.Key ?? contentType?.Name,
            };
            if (item.ParentId is { } parent && values.ContainsKey(parent))
            {
                entry["parent"] = Key(parent);
            }

            if (item.IsFolder)
            {
                entry["folder"] = true;
            }

            var converted = converter.Convert(values[item.Id], fields.GetValueOrDefault(item.ContentTypeId, []));
            if (!item.IsFolder || converted.Count > 0)
            {
                entry["fields"] = converted; // Folders only when they have values (LST-19).
            }

            entry["created"] = item.CreatedAt;
            entry["createdBy"] = item.CreatedBy is { } creator ? people.GetValueOrDefault(creator) : null;
            entry["modified"] = item.UpdatedAt;
            entry["modifiedBy"] = item.UpdatedBy is { } editor ? people.GetValueOrDefault(editor) : null;
            if (item.HasUniquePermissions)
            {
                entry["permissions"] = new JsonArray([.. grants.Where(g => g.ObjectId == item.Id)
                    .Select(g => (g.PrincipalType == PrincipalType.User ? people : groups).GetValueOrDefault(g.PrincipalId) is { } principal
                        ? new JsonObject { [g.PrincipalType == PrincipalType.User ? "user" : "group"] = principal, ["level"] = g.Level.ToString() }
                        : null)
                    .OfType<JsonObject>()]);
            }

            exported.Add(entry);
        }

        var path = $"content/items-{listId:N}.json";
        await package.WriteJsonAsync(path, new JsonObject { ["items"] = exported }, cancellationToken);
        return new XElement(Element).With("File", path).With("Count", items.Count);
    }

    private static string Key(Guid id) => id.ToString("N");

    /// <summary>Folders before what they contain (items under a missing parent count as top-level).</summary>
    private static List<ListItem> ParentsFirst(List<ListItem> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        var children = items.Where(i => i.ParentId is { } p && ids.Contains(p)).ToLookup(i => i.ParentId!.Value);
        var result = new List<ListItem>(items.Count);
        var queue = new Queue<ListItem>(items.Where(i => i.ParentId is not { } p || !ids.Contains(p)).OrderBy(i => i.Id));
        while (queue.TryDequeue(out var item))
        {
            result.Add(item);
            foreach (var child in children[item.Id].OrderBy(i => i.Id))
            {
                queue.Enqueue(child);
            }
        }

        return result;
    }

    /// <summary>Turns ids in values into names (looked up once for all items).</summary>
    private sealed class ExportConverter(
        IReadOnlyDictionary<Guid, string> termPaths, IReadOnlyDictionary<Guid, IReadOnlyList<string>> labels,
        IReadOnlyDictionary<Guid, string> userNames, IReadOnlyDictionary<Guid, string> lookupLists)
    {
        public static async Task<ExportConverter> CreateAsync(
            List<ListItem> items, Dictionary<Guid, JsonObject> values, Dictionary<Guid, IReadOnlyList<FieldDefinition>> fields,
            ITermStore terms, IUserDirectory users, ListTemplateLookups lookups, CancellationToken ct)
        {
            var termIds = new HashSet<Guid>();
            var keywordIds = new HashSet<Guid>();
            var userIds = new HashSet<Guid>();
            var lookupLists = new Dictionary<Guid, string>();
            foreach (var item in items)
            {
                foreach (var field in fields.GetValueOrDefault(item.ContentTypeId, []))
                {
                    var ids = Ids(values[item.Id][field.Name]);
                    switch (field.Type)
                    {
                        case ManagedMetadataFieldType.TypeName:
                            termIds.UnionWith(ids);
                            break;
                        case KeywordsFieldType.TypeName:
                            keywordIds.UnionWith(ids);
                            break;
                        case "person":
                            userIds.UnionWith(ids);
                            break;
                        case "lookup" when field.LookupListId is { } target && !lookupLists.ContainsKey(target):
                            if (await lookups.ListPathAsync(target, ct) is { } path)
                            {
                                lookupLists[target] = path;
                            }

                            break;
                    }
                }
            }

            return new ExportConverter(
                await terms.GetTermPathsAsync(termIds, ct), await terms.GetLabelsAsync(keywordIds, ct), await users.GetUserNamesAsync(userIds, ct), lookupLists);
        }

        public JsonObject Convert(JsonObject values, IReadOnlyList<FieldDefinition> fields)
        {
            var result = new JsonObject();
            foreach (var field in fields)
            {
                if (values[field.Name] is not { } value)
                {
                    continue;
                }

                var converted = value is JsonArray array
                    ? new JsonArray([.. array.Select(v => One(field, v)).OfType<JsonNode>()])
                    : One(field, value);
                if (converted is not null)
                {
                    result[field.Name] = converted;
                }
            }

            return result;
        }

        private JsonNode? One(FieldDefinition field, JsonNode? value)
        {
            var id = Guid.TryParse((value as JsonValue)?.ToString(), out var parsed) ? parsed : (Guid?)null;
            return field.Type switch
            {
                ManagedMetadataFieldType.TypeName => id is { } t && termPaths.TryGetValue(t, out var path) ? JsonValue.Create(path) : null,
                KeywordsFieldType.TypeName => id is { } k && labels.TryGetValue(k, out var names) && names.Count > 0 ? JsonValue.Create(names[0]) : null,
                "person" => id is { } u && userNames.TryGetValue(u, out var name) ? JsonValue.Create(name) : null,
                "lookup" => id is { } i && field.LookupListId is { } list && lookupLists.TryGetValue(list, out var listPath)
                    ? new JsonObject { ["list"] = listPath, ["key"] = Key(i) }
                    : null,
                _ => value?.DeepClone(),
            };
        }
    }

    internal static IEnumerable<Guid> Ids(JsonNode? value) => value switch
    {
        JsonArray array => array.Select(v => Guid.TryParse((v as JsonValue)?.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty),
        JsonValue single when Guid.TryParse(single.ToString(), out var id) => [id],
        _ => [],
    };

    // ---- Apply ----------------------------------------------------------------

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var package = context.RequirePackage(section);
        var file = section.RequiredAttr("File");
        var document = await package.ReadJsonAsync(file, cancellationToken) ?? throw new TemplateException($"The package has no {file}.", section);
        var entries = (document["items"] as JsonArray ?? throw new TemplateException($"{file} has no items array.", section)).OfType<JsonObject>().ToList();
        if (entries.Any(e => e["key"] is not JsonValue key || string.IsNullOrWhiteSpace(key.ToString())))
        {
            throw new TemplateException($"{file}: every item needs a key.", section);
        }

        var name = $"{context.WorkspaceName}/{context.ListName}";
        if (context.IsPlanned)
        {
            context.Created(Kind, name, $"{entries.Count} items");
            return;
        }

        var listId = context.ListId!.Value;
        var ids = entries.Select(e => TemplateContent.ItemId(listId, e["key"]!.ToString())).ToList();
        var existing = new HashSet<Guid>();
        foreach (var chunk in ids.Chunk(500))
        {
            existing.UnionWith(await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking()
                .Where(i => chunk.Contains(i.Id)).Select(i => i.Id).ToListAsync(cancellationToken));
        }

        var missing = entries.Where((_, i) => !existing.Contains(ids[i])).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        context.Created(Kind, name, $"{missing.Count} items");
        if (context.DryRun)
        {
            return;
        }

        var schema = (await loader.LoadAsSystemAsync(context.WorkspaceId!.Value, listId, cancellationToken))!;
        var lookupsToSet = new List<(Guid Id, JsonObject Values)>();
        foreach (var entry in missing)
        {
            var key = entry["key"]!.ToString();
            var reference = entry["contentType"]?.ToString();
            var contentType = reference is null ? schema.DefaultContentType
                : schema.ContentTypes.FirstOrDefault(c => c.Key == reference) ?? schema.ContentTypes.FirstOrDefault(c => c.Name == reference);
            if (contentType is null)
            {
                context.Warn($"{name}: item {key} was skipped: the list has no content type '{reference}'.", section);
                continue;
            }

            var isFolder = entry["folder"] is JsonValue folder && folder.TryGetValue<bool>(out var flag) && flag;
            var (values, lookupValues) = await ImportAsync(entry["fields"] as JsonObject, contentType.Fields, context, name, cancellationToken);
            values["title"] = entry["title"]?.DeepClone();
            var parentId = entry["parent"] is JsonValue parent ? TemplateContent.ItemId(listId, parent.ToString()) : (Guid?)null;
            var id = TemplateContent.ItemId(listId, key);
            if (await StampAsync(entry, cancellationToken) is { } stamp)
            {
                stamps.Set(id, stamp);
            }

            var permissions = entry["permissions"] is JsonArray granted ? await GrantsAsync(granted, context, name, key, cancellationToken) : null;
            using var json = JsonDocument.Parse(values.ToJsonString());
            var result = await writer.CreateAsync(schema, contentType.Id, parentId, isFolder, json.RootElement, id, cancellationToken, permissions);
            if (result.Item is null)
            {
                context.Warn($"{name}: item {key} ('{entry["title"]}') was skipped: {Describe(result)}", section);
                continue;
            }

            if (lookupValues.Count > 0)
            {
                lookupsToSet.Add((id, lookupValues));
            }
        }

        if (lookupsToSet.Count > 0)
        {
            var workspaceId = context.WorkspaceId!.Value;
            context.Defer(ct => SetLookupsAsync(workspaceId, listId, name, lookupsToSet, context, ct));
        }
    }

    /// <summary>The original created and changed stamps of an entry, when it has them (authors by user name).</summary>
    private async Task<AuditStamp?> StampAsync(JsonObject entry, CancellationToken ct)
    {
        if (entry["created"] is not JsonValue created || !created.TryGetValue<DateTimeOffset>(out var createdAt))
        {
            return null;
        }

        async Task<Guid?> UserAsync(string property) =>
            entry[property]?.ToString() is { Length: > 0 } userName ? await users.FindUserAsync(userName, ct) : null;

        DateTimeOffset? modifiedAt = entry["modified"] is JsonValue modified && modified.TryGetValue<DateTimeOffset>(out var at) ? at : null;
        return new AuditStamp(createdAt, await UserAsync("createdBy"), modifiedAt, await UserAsync("modifiedBy"));
    }

    /// <summary>Unique permissions of an entry: principals by name; unknown ones are left out with a warning.</summary>
    private async Task<List<PermissionGrantDto>> GrantsAsync(JsonArray entries, TemplateContext context, string name, string key, CancellationToken ct)
    {
        var grants = new List<PermissionGrantDto>();
        foreach (var grant in entries.OfType<JsonObject>())
        {
            var isGroup = grant["group"] is not null;
            var principal = (grant["group"] ?? grant["user"])?.ToString();
            if (principal is null || !Enum.TryParse<Workspaces.Contracts.WorkspaceAccessLevel>(grant["level"]?.ToString(), ignoreCase: true, out var level)
                || level is not (Workspaces.Contracts.WorkspaceAccessLevel.Read or Workspaces.Contracts.WorkspaceAccessLevel.Contribute or Workspaces.Contracts.WorkspaceAccessLevel.Manage))
            {
                context.Warn($"{name}: item {key} has a permission without a user or group and a level (Read, Contribute, Manage).");
                continue;
            }

            var id = isGroup ? await users.FindGroupAsync(principal, ct) : await users.FindUserAsync(principal, ct);
            if (id is null)
            {
                context.Warn($"{name}: item {key}: the {(isGroup ? "group" : "user")} '{principal}' does not exist, so it gets no permission.");
                continue;
            }

            grants.Add(new PermissionGrantDto(isGroup ? PrincipalType.Group : PrincipalType.User, id.Value, level));
        }

        return grants;
    }

    /// <summary>Values of an item from the package: names back to ids; lookups are returned separately.</summary>
    private async Task<(JsonObject Values, JsonObject Lookups)> ImportAsync(
        JsonObject? fields, IReadOnlyList<FieldDefinition> definitions, TemplateContext context, string name, CancellationToken ct)
    {
        var values = new JsonObject();
        var lookupValues = new JsonObject();
        foreach (var (fieldName, value) in fields ?? [])
        {
            // Lookups are recognized by their form: their field may only be completed after the lists (template deferral).
            if (value is JsonObject || (value is JsonArray lookupArray && lookupArray.Count > 0 && lookupArray.All(v => v is JsonObject)))
            {
                lookupValues[fieldName] = value.DeepClone();
                continue;
            }

            var field = definitions.FirstOrDefault(f => f.Name == fieldName);
            if (field is null || value is null)
            {
                continue;
            }

            if (field.Type is ManagedMetadataFieldType.TypeName or "person")
            {
                var converted = new List<JsonNode>();
                foreach (var text in value is JsonArray array ? array.Select(v => v?.ToString()) : [value.ToString()])
                {
                    var id = text is null ? null
                        : field.Type == "person" ? await users.FindUserAsync(text, ct)
                        : context.Resolve(TemplateKinds.Term, text) ?? await terms.FindTermByPathAsync(text, ct);
                    if (id is { } found)
                    {
                        converted.Add(JsonValue.Create(found.ToString())!);
                    }
                    else
                    {
                        context.Warn($"{name}: {(field.Type == "person" ? "user" : "term")} '{text}' of {fieldName} does not exist and was left out.");
                    }
                }

                if (converted.Count > 0)
                {
                    values[fieldName] = value is JsonArray ? new JsonArray([.. converted]) : converted[0];
                }

                continue;
            }

            values[fieldName] = value.DeepClone();
        }

        return (values, lookupValues);
    }

    /// <summary>After all lists are filled: lookup values point to the items created from their keys.</summary>
    private async Task SetLookupsAsync(Guid workspaceId, Guid listId, string name, List<(Guid Id, JsonObject Values)> items, TemplateContext context, CancellationToken ct)
    {
        var schema = (await loader.LoadAsSystemAsync(workspaceId, listId, ct))!;
        foreach (var (id, lookupValues) in items)
        {
            var values = new JsonObject();
            foreach (var (fieldName, value) in lookupValues)
            {
                var targets = new List<JsonNode>();
                foreach (var reference in value is JsonArray array ? array.OfType<JsonObject>() : value is JsonObject single ? [single] : [])
                {
                    if (reference["list"]?.ToString() is { } path && reference["key"]?.ToString() is { } key
                        && await lookups.ListAsync(path, context, ct) is { } targetList)
                    {
                        targets.Add(JsonValue.Create(TemplateContent.ItemId(targetList, key).ToString())!);
                    }
                }

                if (targets.Count > 0)
                {
                    values[fieldName] = value is JsonArray ? new JsonArray([.. targets]) : targets[0];
                }
            }

            var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null || values.Count == 0)
            {
                continue;
            }

            using var json = JsonDocument.Parse(values.ToJsonString());
            var result = await writer.UpdateAsync(schema, item, null, Optional<Guid?>.None, json.RootElement, ct);
            if (result.Item is null)
            {
                context.Warn($"{name}: lookups of item '{item.Title}' were left out: {Describe(result)}");
            }
        }
    }

    private static string Describe(ItemWriteResult result) =>
        result.Errors is { } errors ? string.Join(" ", errors.SelectMany(e => e.Value.Select(v => $"{e.Key}: {v}")))
        : result.Cancelled ?? result.Conflict ?? "not allowed";
}
