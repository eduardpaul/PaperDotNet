using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Messaging;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// List section <c>Items</c> (PRV-04): the list's items and folders, in a JSON document of the template package. Values
/// are portable: terms as <c>Group/Set/Term</c> paths, keywords as text, people as user names, lookups as
/// <c>{ "list": "Workspace/List", "key": … }</c>. Apply creates the items that are missing, each with an id derived from
/// the list and its key (applying again creates nothing twice; existing items are never changed), through the normal
/// write path (validation, mutators, events); lookups are set once every list of the template is filled. Entries also
/// carry when and by whom items were created and changed (<c>created</c>, <c>createdBy</c>, <c>modified</c>,
/// <c>modifiedBy</c>, kept on import through <see cref="AuditOverrides"/>) and unique permissions (<c>permissions</c>:
/// user or group names, or a workspace role, with a level).
/// </summary>
internal sealed class ListItemsTemplateHandler(
    ListsDbContext db, ListSchemaLoader loader, ItemWriter writer, ListTemplateLookups lookups, ITermStore terms, IUserDirectory users,
    AuditOverrides stamps, ScopeMover mover, IOutbox outbox) : ITemplateHandler
{
    public const string Kind = "items";

    private static readonly Dictionary<string, string> RoleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["visitors"] = AclPrincipalTypes.WorkspaceVisitors,
        ["members"] = AclPrincipalTypes.WorkspaceMembers,
    };

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

        var tenantId = context.TenantId;
        var listId = context.ListId!.Value;
        var items = await ItemsAsync(tenantId, listId, cancellationToken);
        if (items.Count == 0)
        {
            return null;
        }

        var schema = (await loader.LoadAsync(ListCaller.From(context.Actor, system: true), context.WorkspaceId!.Value, listId, cancellationToken))!;
        var ordered = ParentsFirst(items);
        var values = ordered.ToDictionary(i => i.Id, i => JsonNode.Parse(i.Fields)!.AsObject());
        var fields = schema.ContentTypes.ToDictionary(c => c.Id, c => c.Fields);
        var converter = await ExportConverter.CreateAsync(tenantId, ordered, values, fields, terms, users, lookups, cancellationToken);
        var grants = ordered.Any(i => i.HasUniquePermissions) ? await GrantsOfListAsync(tenantId, listId, cancellationToken) : [];
        var people = await users.GetUserNamesAsync(tenantId,
            [.. ordered.SelectMany(i => new[] { i.CreatedBy, i.UpdatedBy }).OfType<Guid>().Concat(grants.Where(g => g.PrincipalType == AclPrincipalTypes.User).Select(g => g.PrincipalId)).Distinct()],
            cancellationToken);
        var groups = await users.GetGroupNamesAsync(tenantId, [.. grants.Where(g => g.PrincipalType == AclPrincipalTypes.Group).Select(g => g.PrincipalId).Distinct()], cancellationToken);
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
                // Workspace roles by name (owners are implied), people and groups by name.
                var permissions = new JsonArray();
                foreach (var grant in grants.Where(g => g.ScopeId == item.Id))
                {
                    var level = ((WorkspaceAccessLevel)grant.Level).ToString();
                    if (RoleTypes.FirstOrDefault(r => r.Value == grant.PrincipalType).Key is { } role)
                    {
                        permissions.Add((JsonNode)new JsonObject { ["role"] = role, ["level"] = level });
                    }
                    else if (grant.PrincipalType == AclPrincipalTypes.User && people.GetValueOrDefault(grant.PrincipalId) is { } userName)
                    {
                        permissions.Add((JsonNode)new JsonObject { ["user"] = userName, ["level"] = level });
                    }
                    else if (grant.PrincipalType == AclPrincipalTypes.Group && groups.GetValueOrDefault(grant.PrincipalId) is { } groupName)
                    {
                        context.Require(TemplateKinds.Group, groupName);
                        permissions.Add((JsonNode)new JsonObject { ["group"] = groupName, ["level"] = level });
                    }
                }

                entry["permissions"] = permissions;
            }

            exported.Add((JsonNode)entry);
        }

        var path = $"content/items-{listId:N}.json";
        await package.WriteJsonAsync(path, new JsonObject { ["items"] = exported }, cancellationToken);
        return new XElement(Element).With("File", path).With("Count", items.Count);
    }

    private static string Key(Guid id) => id.ToString("N");

    private async Task<List<ListItem>> ItemsAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var list = listId;
        var ct = cancellationToken;
        return await context.Items.AsNoTracking().Where(i => i.TenantId == tenant && i.ListId == list && i.DeletedAt == null).ToListAsync(ct);
    }

    /// <summary>The entries of the item scopes of a list (not the list's own; owners are implied).</summary>
    private async Task<List<AclEntry>> GrantsOfListAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var list = listId;
        var owners = AclPrincipalTypes.WorkspaceOwners;
        var ct = cancellationToken;
        return await context.AclEntries.AsNoTracking()
            .Where(e => e.TenantId == tenant && e.ListId == list && e.ScopeId != list && e.PrincipalType != owners)
            .ToListAsync(ct);
    }

    /// <summary>Ids of every item of the list, including those in the recycle bin.</summary>
    private async Task<HashSet<Guid>> ExistingIdsAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var list = listId;
        var ct = cancellationToken;
        return [.. await context.Items.AsNoTracking().Where(i => i.TenantId == tenant && i.ListId == list).Select(i => i.Id).ToListAsync(ct)];
    }

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
            Guid tenantId, List<ListItem> items, Dictionary<Guid, JsonObject> values, Dictionary<Guid, IReadOnlyList<FieldDefinition>> fields,
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
                            if (await lookups.ListPathAsync(tenantId, target, ct) is { } path)
                            {
                                lookupLists[target] = path;
                            }

                            break;
                    }
                }
            }

            return new ExportConverter(
                await terms.GetTermPathsAsync(tenantId, termIds, ct), await terms.GetLabelsAsync(tenantId, keywordIds, ct),
                await users.GetUserNamesAsync(tenantId, userIds, ct), lookupLists);
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

    private static IEnumerable<Guid> Ids(JsonNode? value) => value switch
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
        var existing = await ExistingIdsAsync(context.TenantId, listId, cancellationToken);
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

        var caller = ListCaller.From(context.Actor, system: true);
        var workspaceId = context.WorkspaceId!.Value;
        var schema = (await loader.LoadAsync(caller, workspaceId, listId, cancellationToken))!;
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
            if (await StampAsync(context.TenantId, entry, cancellationToken) is { } stamp)
            {
                stamps.Set(id, stamp);
            }

            using var json = JsonDocument.Parse(values.ToJsonString());
            var result = await writer.CreateAsync(caller, schema, contentType.Id, parentId, isFolder, json.RootElement, id, cancellationToken);
            if (result.Item is not { } item)
            {
                context.Warn($"{name}: item {key} ('{entry["title"]}') was skipped: {Describe(result)}", section);
                continue;
            }

            if (entry["permissions"] is JsonArray granted)
            {
                await BreakInheritanceAsync(schema.List, item, await GrantsAsync(granted, context, name, key, cancellationToken), cancellationToken);
            }

            if (lookupValues.Count > 0)
            {
                lookupsToSet.Add((id, lookupValues));
            }
        }

        if (lookupsToSet.Count > 0)
        {
            context.Defer(ct => SetLookupsAsync(caller, workspaceId, listId, name, lookupsToSet, context, ct));
        }
    }

    /// <summary>The original created and changed stamps of an entry, when it has them (authors by user name).</summary>
    private async Task<AuditStamp?> StampAsync(Guid tenantId, JsonObject entry, CancellationToken ct)
    {
        if (entry["created"] is not JsonValue created || !created.TryGetValue<DateTimeOffset>(out var createdAt))
        {
            return null;
        }

        async Task<Guid?> UserAsync(string property) =>
            entry[property]?.ToString() is { Length: > 0 } userName ? await users.FindUserAsync(tenantId, userName, ct) : null;

        DateTimeOffset? modifiedAt = entry["modified"] is JsonValue modified && modified.TryGetValue<DateTimeOffset>(out var at) ? at : null;
        return new AuditStamp(createdAt, await UserAsync("createdBy"), modifiedAt, await UserAsync("modifiedBy"));
    }

    /// <summary>Gives a new item (no children yet) its own permissions; search and delta follow through the scope change.</summary>
    private async Task BreakInheritanceAsync(ListDefinition list, ListItem item, List<PermissionGrantDto> grants, CancellationToken ct)
    {
        var oldScope = item.ScopeId;
        db.AclEntries.AddRange(Acl.FromGrants(list, item.Id, grants));
        item.HasUniquePermissions = true;
        item.ScopeId = item.Id;
        await mover.SaveScopeChangeAsync(outbox, item, oldScope, ct);
    }

    /// <summary>Unique permissions of an entry: principals by name; unknown ones are left out with a warning.</summary>
    private async Task<List<PermissionGrantDto>> GrantsAsync(JsonArray entries, TemplateContext context, string name, string key, CancellationToken ct)
    {
        var grants = new List<PermissionGrantDto>();
        foreach (var grant in entries.OfType<JsonObject>())
        {
            var isGroup = grant["group"] is not null;
            var principal = (grant["group"] ?? grant["user"])?.ToString();
            var role = grant["role"]?.ToString();
            if ((principal is null && role is null) || !Enum.TryParse<WorkspaceAccessLevel>(grant["level"]?.ToString(), ignoreCase: true, out var level)
                || level is not (WorkspaceAccessLevel.Read or WorkspaceAccessLevel.Contribute or WorkspaceAccessLevel.Manage))
            {
                context.Warn($"{name}: item {key} has a permission without a user, group or role and a level (Read, Contribute, Manage).");
                continue;
            }

            if (principal is null)
            {
                if (RoleTypes.TryGetValue(role!, out var roleType))
                {
                    grants.Add(new PermissionGrantDto(roleType, Guid.Empty, level)); // The role of the target list's workspace.
                }
                else if (!string.Equals(role, "owners", StringComparison.OrdinalIgnoreCase))
                {
                    context.Warn($"{name}: item {key}: the role '{role}' is not visitors, members or owners.");
                }

                continue;
            }

            var id = isGroup
                ? context.Resolve(TemplateKinds.Group, principal) ?? await users.FindGroupAsync(context.TenantId, principal, ct)
                : await users.FindUserAsync(context.TenantId, principal, ct);
            if (id is null)
            {
                context.Warn($"{name}: item {key}: the {(isGroup ? "group" : "user")} '{principal}' does not exist, so it gets no permission.");
                continue;
            }

            grants.Add(new PermissionGrantDto(isGroup ? AclPrincipalTypes.Group : AclPrincipalTypes.User, id.Value, level));
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
                        : field.Type == "person" ? await users.FindUserAsync(context.TenantId, text, ct)
                        : context.Resolve(TemplateKinds.Term, text) ?? await terms.FindTermByPathAsync(context.TenantId, text, ct);
                    if (id is { } found)
                    {
                        converted.Add(JsonValue.Create(found.ToString()));
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
    private async Task SetLookupsAsync(
        ListCaller caller, Guid workspaceId, Guid listId, string name, List<(Guid Id, JsonObject Values)> items, TemplateContext context, CancellationToken ct)
    {
        var schema = (await loader.LoadAsync(caller, workspaceId, listId, ct))!;
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
                        targets.Add(JsonValue.Create(TemplateContent.ItemId(targetList, key).ToString()));
                    }
                }

                if (targets.Count > 0)
                {
                    values[fieldName] = value is JsonArray ? new JsonArray([.. targets]) : targets[0];
                }
            }

            var item = await ItemEndpoints.FindTrackedAsync(db, caller.TenantId, listId, id, ct);
            if (item is null || values.Count == 0)
            {
                continue;
            }

            using var json = JsonDocument.Parse(values.ToJsonString());
            var result = await writer.UpdateAsync(caller, schema, item, null, Optional<Guid?>.None, json.RootElement, ct);
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
