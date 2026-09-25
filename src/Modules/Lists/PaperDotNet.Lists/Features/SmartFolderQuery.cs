using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Runs smart folder definitions over the caller's lists.</summary>
internal sealed class SmartFolderQuery(
    ListSchemaLoader loader, ItemQueryRunner runner, IWorkspaceAccess workspaces, IListItemStore items,
    ITermStore terms, IUserDirectory users, FieldTypeRegistry fieldTypes)
{
    internal sealed record Candidate(Guid WorkspaceId, ListSchema Schema);

    internal sealed record Found(Guid WorkspaceId, string ListName, ListItem Item);

    /// <summary>The lists the folder covers that the caller can read (at most <see cref="SmartFolders.MaxLists"/>).</summary>
    public async Task<List<Candidate>> ListsAsync(SmartFolder folder, SmartFolderDefinition definition, CancellationToken ct)
    {
        var memberships = await workspaces.GetMyWorkspacesAsync(ct);
        var workspaceIds = folder.WorkspaceId is { } ws
            ? memberships.Where(m => m.WorkspaceId == ws).ToList()
            : memberships.ToList();
        var visible = new List<ListDefinition>();
        foreach (var membership in workspaceIds)
        {
            foreach (var list in await loader.VisibleListsAsync(membership.WorkspaceId, membership.Level, ct))
            {
                if (definition.Lists is { Count: > 0 } names && !names.Contains(list.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (definition.ListTemplates is { Count: > 0 } templates && !templates.Contains(list.TemplateKey ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                visible.Add(list);
            }
        }

        if (definition.ContentTypes is { Count: > 0 } wanted)
        {
            var types = await loader.ContentTypesAsync(visible.SelectMany(l => l.ContentTypeIds), ct);
            visible = visible.Where(list => list.ContentTypeIds.Any(id =>
                types.TryGetValue(id, out var type) && wanted.Any(name =>
                    string.Equals(name, type.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(name, type.Key, StringComparison.OrdinalIgnoreCase)))).ToList();
        }

        var candidates = new List<Candidate>();
        foreach (var list in visible)
        {
            if (await loader.LoadAsync(list.WorkspaceId, list.Id, ct) is not { } schema)
            {
                continue;
            }

            if (definition.ContentTypes is { Count: > 0 } && ContentTypeIds(schema, definition).Count == 0)
            {
                continue;
            }

            candidates.Add(new Candidate(list.WorkspaceId, schema));
            if (candidates.Count >= SmartFolders.MaxLists)
            {
                return candidates;
            }
        }

        return candidates;
    }

    public async Task<(List<Found> Items, string? Error)> ItemsAsync(
        SmartFolder folder, SmartFolderDefinition definition, string?[] path, (DateTimeOffset At, Guid Id)? after, int take, CancellationToken ct)
    {
        var termInfo = await TermsAsync(definition, ct);
        var found = new List<Found>();
        foreach (var candidate in await ListsAsync(folder, definition, ct))
        {
            if (Filter(candidate.Schema, definition, termInfo) is not { } filter)
            {
                continue;
            }

            var (listItems, error) = await RecentAsync(candidate.Schema, filter.Length == 0 ? null : filter, Extra(definition, path), after, take, ct);
            if (error is not null)
            {
                return ([], error);
            }

            found.AddRange(listItems!.Select(i => new Found(candidate.WorkspaceId, candidate.Schema.List.Name, i)));
        }

        return (found.OrderByDescending(f => f.Item.UpdatedAt).ThenByDescending(f => f.Item.Id).Take(take).ToList(), null);
    }

    public async Task<(List<SmartFolderGroup> Groups, string? Error)> GroupsAsync(
        SmartFolder folder, SmartFolderDefinition definition, string?[] path, SmartFolderGroupBy level, CancellationToken ct)
    {
        var termInfo = await TermsAsync(definition, ct);
        var counts = new Dictionary<string, int>();
        var empty = 0;
        FieldDefinition? sample = null;
        foreach (var candidate in await ListsAsync(folder, definition, ct))
        {
            if (Filter(candidate.Schema, definition, termInfo) is not { } filter)
            {
                continue;
            }

            if (candidate.Schema.Fields.TryGetValue(level.Field, out var field) && !SmartFolders.IsGroupable(field, fieldTypes))
            {
                continue;
            }

            sample ??= field;
            var (groups, none, error) = await GroupAsync(candidate.Schema, filter.Length == 0 ? null : filter, Extra(definition, path), ItemFields.GroupKey(level.Field, level.By), ct);
            if (error is not null)
            {
                return ([], error);
            }

            foreach (var (value, count) in groups!)
            {
                counts[value] = counts.GetValueOrDefault(value) + count;
            }

            empty += none;
        }

        var labels = await LabelsAsync(sample, counts.Keys.ToList(), ct);
        var result = counts
            .Select(c => new SmartFolderGroup(c.Key, labels.GetValueOrDefault(c.Key, c.Key), c.Value))
            .OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (empty > 0)
        {
            result.Add(new SmartFolderGroup(null, "(empty)", empty));
        }

        return (result, null);
    }

    /// <summary>Applies the folder's classification to an existing or new item of one of its lists.</summary>
    public async Task<(ListItemResult? Result, bool Created, string? Error)> ClassifyAsync(
        SmartFolder folder, SmartFolderDefinition definition, SmartFolderDropRequest request, CancellationToken ct)
    {
        var candidate = (await ListsAsync(folder, definition, ct)).FirstOrDefault(c => c.Schema.List.Id == request.ListId && c.WorkspaceId == request.WorkspaceId);
        if (candidate is null)
        {
            return (null, false, "The list is not part of this smart folder.");
        }

        ListItemData? current = null;
        if (request.ItemId is { } itemId)
        {
            current = await items.GetAsync(request.WorkspaceId, request.ListId, itemId, ct);
            if (current is null)
            {
                return (new ListItemResult(ListItemStatus.NotFound), false, null);
            }
        }

        var (values, error) = await ClassificationAsync(candidate.Schema, definition, request.Path ?? [], current?.Fields ?? request.Fields!, add: true, ct);
        if (values is null)
        {
            return (null, false, error);
        }

        if (current is not null)
        {
            return (await items.UpdateAsync(request.WorkspaceId, request.ListId, current.Id, values, null, ct), false, null);
        }

        var fields = request.Fields!.DeepClone().AsObject();
        foreach (var (name, value) in values)
        {
            fields[name] = value?.DeepClone();
        }

        var contentTypeId = definition.ContentTypes is { Count: > 0 } ? ContentTypeIds(candidate.Schema, definition).FirstOrDefault() : (Guid?)null;
        return (await items.CreateAsync(request.WorkspaceId, request.ListId, fields, contentTypeId, ct), true, null);
    }

    public async Task<(ListItemResult? Result, string? Error)> UnclassifyAsync(
        SmartFolder folder, SmartFolderDefinition definition, Guid workspaceId, Guid listId, Guid itemId, CancellationToken ct)
    {
        var candidate = (await ListsAsync(folder, definition, ct)).FirstOrDefault(c => c.Schema.List.Id == listId && c.WorkspaceId == workspaceId);
        if (candidate is null)
        {
            return (null, "The list is not part of this smart folder.");
        }

        var current = await items.GetAsync(workspaceId, listId, itemId, ct);
        if (current is null)
        {
            return (new ListItemResult(ListItemStatus.NotFound), null);
        }

        var (values, error) = await ClassificationAsync(candidate.Schema, definition, [], current.Fields, add: false, ct);
        return values is null ? (null, error) : (await items.UpdateAsync(workspaceId, listId, itemId, values, null, ct), null);
    }

    private async Task<(List<ListItem>? Items, string? Error)> RecentAsync(
        ListSchema schema, string? filter, Expression<Func<ListItem, bool>>? extra, (DateTimeOffset At, Guid Id)? after, int take, CancellationToken ct)
    {
        var (query, error) = await runner.MatchingAsync(schema, filter, extra, ct);
        if (query is null)
        {
            return (null, error);
        }

        if (after is { } position)
        {
            var at = position.At;
            var id = position.Id;
            query = query.Where(i => i.UpdatedAt < at || (i.UpdatedAt == at && i.Id.CompareTo(id) < 0));
        }

        return (await query.OrderByDescending(i => i.UpdatedAt).ThenByDescending(i => i.Id).Take(take).ToListAsync(ct), null);
    }

    private async Task<(Dictionary<string, int>? Groups, int Empty, string? Error)> GroupAsync(
        ListSchema schema, string? filter, Expression<Func<ListItem, bool>>? extra, Expression<Func<ListItem, string?>> key, CancellationToken ct)
    {
        var (query, error) = await runner.MatchingAsync(schema, filter, extra, ct);
        if (query is null)
        {
            return (null, 0, error);
        }

        var groups = await query.GroupBy(key).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return (groups.Where(g => g.Key is not null).ToDictionary(g => g.Key!, g => g.Count), groups.Where(g => g.Key is null).Sum(g => g.Count), null);
    }

    /// <summary>
    /// Field changes that put an item into the folder (<paramref name="add"/>) or take it out: its terms go into
    /// the matching term fields, <c>eq</c> conditions and sub-folder values become field values.
    /// </summary>
    private async Task<(JsonObject? Values, string? Error)> ClassificationAsync(
        ListSchema schema, SmartFolderDefinition definition, IReadOnlyList<string?> path, JsonObject current, bool add, CancellationToken ct)
    {
        var values = new JsonObject();
        foreach (var term in await TermsAsync(definition, ct))
        {
            var field = schema.Fields.Values.FirstOrDefault(f =>
                (f.Type == ManagedMetadataFieldType.TypeName && f.TermSetId == term.TermSetId) || (f.Type == KeywordsFieldType.TypeName && term.IsKeyword));
            if (field is null)
            {
                return (null, $"The list '{schema.List.Name}' has no field for the term '{term.Name}'.");
            }

            var id = term.Id.ToString();
            if (field.AllowMultiple)
            {
                var existing = (values[field.Name] ?? current[field.Name]) is JsonArray array
                    ? array.Select(v => v?.GetValue<string>()).Where(v => v is not null).Select(v => v!).ToList()
                    : [];
                existing = add ? [.. existing.Where(v => v != id), id] : existing.Where(v => v != id).ToList();
                values[field.Name] = new JsonArray([.. existing.Select(v => (JsonNode?)JsonValue.Create(v))]);
            }
            else if (add)
            {
                values[field.Name] = id;
            }
            else if (current[field.Name]?.GetValue<string>() == id)
            {
                values[field.Name] = null;
            }
        }

        var (translator, clause, error) = runner.TryFilter(schema, definition.Filter);
        if (error is not null)
        {
            return (null, error);
        }

        foreach (var (name, value) in clause is null ? [] : translator!.Equalities(clause))
        {
            if (add)
            {
                values[name] = value?.DeepClone();
            }
            else if (value is not null && JsonNode.DeepEquals(current[name], value) && name != "title")
            {
                values[name] = null;
            }
        }

        for (var level = 0; add && level < path.Count && level < (definition.GroupBy?.Count ?? 0); level++)
        {
            var group = definition.GroupBy![level];
            if (group.By is null && schema.Fields.ContainsKey(group.Field))
            {
                values[group.Field] = path[level] is { Length: > 0 } value ? value : null;
            }
        }

        return (values, null);
    }

    /// <summary>The OData filter of the folder for one list; null when the list cannot match (e.g. no term field).</summary>
    private static string? Filter(ListSchema schema, SmartFolderDefinition definition, IReadOnlyList<TermInfo> termInfo)
    {
        var parts = new List<string>();
        if (!definition.IncludeFolders)
        {
            parts.Add("isFolder eq false");
        }

        if (definition.ContentTypes is { Count: > 0 })
        {
            parts.Add("(" + string.Join(" or ", ContentTypeIds(schema, definition).Select(id => $"contentTypeId eq {id}")) + ")");
        }

        var termConditions = new List<string>();
        foreach (var term in termInfo)
        {
            var fields = schema.Fields.Values
                .Where(f => (f.Type == ManagedMetadataFieldType.TypeName && f.TermSetId == term.TermSetId) || (f.Type == KeywordsFieldType.TypeName && term.IsKeyword))
                .Select(f => f.AllowMultiple ? $"fields/{f.Name}/any(t: t eq {term.Id})" : $"fields/{f.Name} eq {term.Id}")
                .ToList();
            if (fields.Count > 0)
            {
                termConditions.Add("(" + string.Join(" or ", fields) + ")");
            }
            else if (definition.TermMatch != "any")
            {
                return null;
            }
        }

        if (termInfo.Count > 0)
        {
            if (termConditions.Count == 0)
            {
                return null;
            }

            parts.Add("(" + string.Join(definition.TermMatch == "any" ? " or " : " and ", termConditions) + ")");
        }

        if (!string.IsNullOrWhiteSpace(definition.Filter))
        {
            parts.Add("(" + definition.Filter + ")");
        }

        return string.Join(" and ", parts);
    }

    /// <summary>Conditions of the sub-folder <paramref name="path"/> (values of the groupBy levels).</summary>
    private static Expression<Func<ListItem, bool>>? Extra(SmartFolderDefinition definition, string?[] path)
    {
        Expression<Func<ListItem, bool>>? result = null;
        for (var level = 0; level < path.Length && level < (definition.GroupBy?.Count ?? 0); level++)
        {
            var group = definition.GroupBy![level];
            var condition = path[level] is { Length: > 0 } value
                ? ItemFields.GroupEquals(group.Field, group.By, value)
                : ItemFields.Missing(group.Field);
            result = result is null ? condition : ItemFields.And(result, condition);
        }

        return result;
    }

    private static List<Guid> ContentTypeIds(ListSchema schema, SmartFolderDefinition definition) =>
        schema.ContentTypes
            .Where(c => definition.ContentTypes!.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(n, c.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Id)
            .ToList();

    private async Task<IReadOnlyList<TermInfo>> TermsAsync(SmartFolderDefinition definition, CancellationToken ct) =>
        definition.Terms is { Count: > 0 } ids ? await terms.GetTermsAsync(ids, ct) : [];

    /// <summary>Display names for group values: term and user names for managed metadata and person fields.</summary>
    private async Task<Dictionary<string, string>> LabelsAsync(FieldDefinition? field, List<string> values, CancellationToken ct)
    {
        var ids = values.Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null).Where(id => id is not null).Select(id => id!.Value).ToList();
        if (field is null || ids.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<Guid, string> names = field.Type switch
        {
            ManagedMetadataFieldType.TypeName => (await terms.GetTermsAsync(ids, ct)).ToDictionary(t => t.Id, t => t.Name),
            "person" => await users.GetUserNamesAsync(ids, ct),
            _ => new Dictionary<Guid, string>(),
        };
        return values.Where(v => Guid.TryParse(v, out var id) && names.ContainsKey(id)).ToDictionary(v => v, v => names[Guid.Parse(v)]);
    }
}
