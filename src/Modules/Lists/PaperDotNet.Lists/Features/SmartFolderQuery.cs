using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.OData;
using Microsoft.OData.UriParser;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// Runs smart folder definitions over the caller's lists: each list runs the folder's OData filter (terms match their
/// child terms, see <see cref="TermHierarchy"/>), and the pages are merged newest first.
/// </summary>
internal sealed class SmartFolderQuery(
    ListSchemaLoader loader, ItemQueryRunner runner, IItemQueries queries, IWorkspaceAccess workspaces, IListItemStore items,
    ITermStore terms, IUserDirectory users, FieldTypeRegistry fieldTypes, TimeProvider time)
{
    /// <summary>Values counted per list for groups (distinct values beyond it are left out).</summary>
    private const int MaxGroupValues = 1000;

    /// <summary>Field kinds that can be grouped by (single values stored as text).</summary>
    private static readonly HashSet<FieldValueKind> GroupableKinds = [FieldValueKind.Text, FieldValueKind.Date, FieldValueKind.DateTime, FieldValueKind.Identifier];

    internal sealed record Found(Guid WorkspaceId, string ListName, ListItem Item);

    /// <summary>The lists the folder covers that the caller can read (at most <see cref="SmartFolders.MaxLists"/>).</summary>
    public async Task<List<ListSchema>> ListsAsync(ListCaller caller, SmartFolder folder, SmartFolderDefinition definition, CancellationToken ct)
    {
        var memberships = caller.UserId is { } user ? await workspaces.GetMembershipsAsync(caller.TenantId, user, ct) : [];
        var scoped = folder.WorkspaceId is { } ws ? [.. memberships.Where(m => m.WorkspaceId == ws)] : memberships;
        var visible = (await loader.VisibleListsAsync(caller, scoped, ct))
            .Where(list => definition.Lists is not { Count: > 0 } names || names.Contains(list.Name, StringComparer.OrdinalIgnoreCase))
            .Where(list => definition.ListTemplates is not { Count: > 0 } templates || templates.Contains(list.TemplateKey ?? "", StringComparer.OrdinalIgnoreCase));
        var schemas = new List<ListSchema>();
        foreach (var list in visible)
        {
            if (await loader.LoadAsync(caller, list.WorkspaceId, list.Id, ct) is not { } schema
                || (definition.ContentTypes is { Count: > 0 } && ContentTypeIds(schema, definition).Count == 0))
            {
                continue;
            }

            schemas.Add(schema);
            if (schemas.Count >= SmartFolders.MaxLists)
            {
                break;
            }
        }

        return schemas;
    }

    public async Task<(List<Found> Items, string? Error)> ItemsAsync(
        ListCaller caller, SmartFolder folder, SmartFolderDefinition definition, string?[] path, (DateTimeOffset At, Guid Id)? after, int take, CancellationToken ct)
    {
        var termInfo = await TermsAsync(caller.TenantId, definition, ct);
        var found = new List<Found>();
        string? firstError = null;
        var queried = 0;
        foreach (var schema in await ListsAsync(caller, folder, definition, ct))
        {
            if (Filter(schema, definition, termInfo) is not { } filter)
            {
                continue;
            }

            var (result, error) = await runner.RunAsync(
                caller, schema, [filter, PathFilter(schema, definition, path), Cursor(after)], "updatedAt desc,id desc", take, null, false,
                definition.IncludeFolders ? FolderMode.All : FolderMode.ItemsOnly, null, ct);
            if (result is null)
            {
                // A list without a field of the filter cannot hold matching items: skip it. The error is reported
                // only when no list could run the filter (e.g. a syntax error).
                firstError ??= error;
                continue;
            }

            queried++;
            found.AddRange(result.Items.Select(i => new Found(schema.List.WorkspaceId, schema.List.Name, i)));
        }

        return queried == 0 && firstError is not null
            ? ([], firstError)
            : ([.. found.OrderByDescending(f => f.Item.UpdatedAt).ThenByDescending(f => f.Item.Id).Take(take)], null);
    }

    public async Task<(List<SmartFolderGroup> Groups, string? Error)> GroupsAsync(
        ListCaller caller, SmartFolder folder, SmartFolderDefinition definition, string?[] path, SmartFolderGroupBy level, CancellationToken ct)
    {
        var termInfo = await TermsAsync(caller.TenantId, definition, ct);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long empty = 0;
        string? firstError = null;
        var queried = 0;
        FieldDefinition? sample = null;
        foreach (var schema in await ListsAsync(caller, folder, definition, ct))
        {
            if (!schema.Fields.TryGetValue(level.Field, out var field) || !IsGroupable(field) || Filter(schema, definition, termInfo) is not { } filter)
            {
                continue;
            }

            sample ??= field;
            var (parsed, error) = await runner.ParseAsync(caller.TenantId, schema, [filter, PathFilter(schema, definition, path)], null, caller.UserId, ct);
            if (parsed is null)
            {
                firstError ??= error;
                continue;
            }

            var query = new ItemQuery(
                caller.TenantId, [schema.List.Id], parsed, schema.Access.Scopes(WorkspaceAccessLevel.Read),
                definition.IncludeFolders ? FolderMode.All : FolderMode.ItemsOnly, null, default, MaxGroupValues, false, null, FieldIndex.Ready(schema.List));
            IReadOnlyList<ValueCount> values;
            try
            {
                values = await queries.CountValuesAsync(query, field.Name, multiple: false, ct);
            }
            catch (Exception ex) when (ex is NotSupportedException or ODataException)
            {
                firstError ??= ex.Message;
                continue;
            }

            queried++;
            foreach (var value in values)
            {
                if (Bucket(value.Value, level.By) is { } key)
                {
                    counts[key] = counts.GetValueOrDefault(key) + value.Count;
                }
                else
                {
                    empty += value.Count;
                }
            }
        }

        if (queried == 0 && firstError is not null)
        {
            return ([], firstError);
        }

        var labels = await LabelsAsync(caller.TenantId, sample, [.. counts.Keys], ct);
        var result = counts
            .Select(c => new SmartFolderGroup(c.Key, labels.GetValueOrDefault(c.Key, c.Key), (int)c.Value))
            .OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (empty > 0)
        {
            result.Add(new SmartFolderGroup(null, "(empty)", (int)empty));
        }

        return (result, null);
    }

    /// <summary>Applies the folder's classification to an existing or new item of one of its lists.</summary>
    public async Task<(ListItemResult? Result, bool Created, string? Error)> ClassifyAsync(
        ListCaller caller, SmartFolder folder, SmartFolderDefinition definition, SmartFolderDropRequest request, CancellationToken ct)
    {
        var schema = (await ListsAsync(caller, folder, definition, ct)).FirstOrDefault(s => s.List.Id == request.ListId && s.List.WorkspaceId == request.WorkspaceId);
        if (schema is null)
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

        var (values, error) = await ClassificationAsync(caller, schema, definition, request.Path ?? [], current?.Fields ?? request.Fields!, add: true, ct);
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

        var contentTypeId = definition.ContentTypes is { Count: > 0 } ? ContentTypeIds(schema, definition).FirstOrDefault() : (Guid?)null;
        return (await items.CreateAsync(request.WorkspaceId, request.ListId, fields, contentTypeId, ct), true, null);
    }

    public async Task<(ListItemResult? Result, string? Error)> UnclassifyAsync(
        ListCaller caller, SmartFolder folder, SmartFolderDefinition definition, Guid workspaceId, Guid listId, Guid itemId, CancellationToken ct)
    {
        var schema = (await ListsAsync(caller, folder, definition, ct)).FirstOrDefault(s => s.List.Id == listId && s.List.WorkspaceId == workspaceId);
        if (schema is null)
        {
            return (null, "The list is not part of this smart folder.");
        }

        var current = await items.GetAsync(workspaceId, listId, itemId, ct);
        if (current is null)
        {
            return (new ListItemResult(ListItemStatus.NotFound), null);
        }

        var (values, error) = await ClassificationAsync(caller, schema, definition, [], current.Fields, add: false, ct);
        return values is null ? (null, error) : (await items.UpdateAsync(workspaceId, listId, itemId, values, null, ct), null);
    }

    /// <summary>
    /// Field changes that put an item into the folder (<paramref name="add"/>) or take it out: its terms go into
    /// the matching term fields, <c>eq</c> conditions and sub-folder values become field values.
    /// </summary>
    private async Task<(JsonObject? Values, string? Error)> ClassificationAsync(
        ListCaller caller, ListSchema schema, SmartFolderDefinition definition, IReadOnlyList<string?> path, JsonObject current, bool add, CancellationToken ct)
    {
        var values = new JsonObject();
        foreach (var term in await TermsAsync(caller.TenantId, definition, ct))
        {
            var field = schema.Fields.Values.FirstOrDefault(f => HoldsTerm(f, term));
            if (field is null)
            {
                return (null, $"The list '{schema.List.Name}' has no field for the term '{term.Name}'.");
            }

            var id = term.Id.ToString();
            if (field.AllowMultiple)
            {
                List<string> existing = (values[field.Name] ?? current[field.Name]) is JsonArray array
                    ? [.. array.Select(v => v is JsonValue s && s.TryGetValue<string>(out var text) ? text : null).OfType<string>()]
                    : [];
                existing = add ? [.. existing.Where(v => v != id), id] : [.. existing.Where(v => v != id)];
                values[field.Name] = new JsonArray([.. existing.Select(v => (JsonNode?)JsonValue.Create(v))]);
            }
            else if (add)
            {
                values[field.Name] = id;
            }
            else if (current[field.Name] is JsonValue single && single.TryGetValue<string>(out var text) && text == id)
            {
                values[field.Name] = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(definition.Filter))
        {
            var (parsed, error) = ItemQueryParser.Parse(schema.Fields, fieldTypes, [definition.Filter], null, caller.UserId, time.GetUtcNow());
            if (parsed is null)
            {
                return (null, error);
            }

            foreach (var (clause, aliases) in parsed.Filters)
            {
                foreach (var (name, value) in Equalities(clause.Expression, aliases))
                {
                    if (add)
                    {
                        values[name] = value?.DeepClone();
                    }
                    else if (value is not null && name != "title" && JsonNode.DeepEquals(current[name], value))
                    {
                        values[name] = null;
                    }
                }
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

    /// <summary>The <c>fields/x eq value</c> conditions joined by <c>and</c> (what an item must hold to match).</summary>
    private static IEnumerable<(string Name, JsonNode? Value)> Equalities(QueryNode node, IDictionary<string, QueryNode> aliases)
    {
        switch (Unwrap(node, aliases))
        {
            case BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And } and:
                foreach (var pair in Equalities(and.Left, aliases).Concat(Equalities(and.Right, aliases)))
                {
                    yield return pair;
                }

                break;
            case BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Equal } equal:
                var (left, right) = (Unwrap(equal.Left, aliases), Unwrap(equal.Right, aliases));
                if (left is ConstantNode && right is not ConstantNode)
                {
                    (left, right) = (right, left);
                }

                if (left is SingleValuePropertyAccessNode { Source: SingleComplexNode } property && right is ConstantNode constant
                    && Json(constant.Value) is var (ok, value) && ok)
                {
                    yield return (property.Property.Name, value);
                }

                break;
        }
    }

    private static QueryNode Unwrap(QueryNode node, IDictionary<string, QueryNode> aliases) => node switch
    {
        ConvertNode convert => Unwrap(convert.Source, aliases),
        ParameterAliasNode alias when aliases.TryGetValue(alias.Alias, out var value) && value is not null => Unwrap(value, aliases),
        _ => node,
    };

    private static (bool Ok, JsonNode? Value) Json(object? value) => value switch
    {
        null => (true, null),
        string text => (true, JsonValue.Create(text)),
        bool flag => (true, JsonValue.Create(flag)),
        decimal number => (true, JsonValue.Create(number)),
        int number => (true, JsonValue.Create(number)),
        long number => (true, JsonValue.Create(number)),
        double number => (true, JsonValue.Create(number)),
        Guid id => (true, JsonValue.Create(id.ToString())),
        DateOnly date => (true, JsonValue.Create(FieldFormats.Date(date))),
        DateTimeOffset at => (true, JsonValue.Create(FieldFormats.DateTime(at))),
        _ => (false, null),
    };

    /// <summary>The OData filter of the folder for one list; null when the list cannot match (e.g. no term field).</summary>
    private static string? Filter(ListSchema schema, SmartFolderDefinition definition, IReadOnlyList<TermInfo> termInfo)
    {
        var parts = new List<string> { "true" };
        if (definition.ContentTypes is { Count: > 0 })
        {
            parts.Add("(" + string.Join(" or ", ContentTypeIds(schema, definition).Select(id => $"contentTypeId eq {id}")) + ")");
        }

        var termConditions = new List<string>();
        foreach (var term in termInfo)
        {
            var fields = schema.Fields.Values
                .Where(f => HoldsTerm(f, term))
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

    /// <summary>Conditions of the sub-folder <paramref name="path"/> (values of the groupBy levels), as OData.</summary>
    private string? PathFilter(ListSchema schema, SmartFolderDefinition definition, string?[] path)
    {
        var parts = new List<string>();
        for (var level = 0; level < path.Length && level < (definition.GroupBy?.Count ?? 0); level++)
        {
            var group = definition.GroupBy![level];
            var kind = schema.Fields.TryGetValue(group.Field, out var field) ? fieldTypes.Find(field.Type)?.ValueKind : null;
            var name = $"fields/{group.Field}";
            if (path[level] is not { Length: > 0 } value)
            {
                parts.Add($"{name} eq null");
                continue;
            }

            if (group.By is "year" or "month" && kind is FieldValueKind.Date or FieldValueKind.DateTime)
            {
                if (!DateOnly.TryParseExact(group.By == "year" ? $"{value}-01-01" : $"{value}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                {
                    parts.Add("false");
                    continue;
                }

                var end = group.By == "year" ? start.AddYears(1) : start.AddMonths(1);
                string Literal(DateOnly d) => kind == FieldValueKind.Date
                    ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z";
                parts.Add($"({name} ge {Literal(start)} and {name} lt {Literal(end)})");
                continue;
            }

            parts.Add(kind switch
            {
                FieldValueKind.Identifier when Guid.TryParse(value, out var id) => $"{name} eq {id}",
                FieldValueKind.Identifier => "false",
                FieldValueKind.Date or FieldValueKind.DateTime or FieldValueKind.Number or FieldValueKind.Boolean => $"{name} eq {value}",
                _ => $"{name} eq '{value.Replace("'", "''", StringComparison.Ordinal)}'",
            });
        }

        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }

    /// <summary>Items changed before the cursor (newest first, then by id).</summary>
    private static string? Cursor((DateTimeOffset At, Guid Id)? after)
    {
        if (after is not { } position)
        {
            return null;
        }

        var at = position.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        return $"updatedAt lt {at} or (updatedAt eq {at} and id lt {position.Id})";
    }

    private static string? Bucket(string? value, string? by) => value switch
    {
        null or "" => null,
        { Length: >= 4 } when by == "year" => value[..4],
        { Length: >= 7 } when by == "month" => value[..7],
        _ => value,
    };

    private bool IsGroupable(FieldDefinition field) =>
        !field.AllowMultiple && fieldTypes.Find(field.Type) is { } type && GroupableKinds.Contains(type.ValueKind);

    private static bool HoldsTerm(FieldDefinition field, TermInfo term) =>
        (field.Type == ManagedMetadataFieldType.TypeName && field.TermSetId == term.TermSetId) || (field.Type == KeywordsFieldType.TypeName && term.IsKeyword);

    private static List<Guid> ContentTypeIds(ListSchema schema, SmartFolderDefinition definition) =>
        [.. schema.ContentTypes
            .Where(c => definition.ContentTypes!.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(n, c.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Id)];

    private async Task<IReadOnlyList<TermInfo>> TermsAsync(Guid tenantId, SmartFolderDefinition definition, CancellationToken ct) =>
        definition.Terms is { Count: > 0 } ids ? await terms.GetTermsAsync(tenantId, ids, ct) : [];

    /// <summary>Display names for group values: term and user names for managed metadata and person fields.</summary>
    private async Task<Dictionary<string, string>> LabelsAsync(Guid tenantId, FieldDefinition? field, List<string> values, CancellationToken ct)
    {
        var ids = values.Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null).OfType<Guid>().ToList();
        if (field is null || ids.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<Guid, string> names = field.Type switch
        {
            ManagedMetadataFieldType.TypeName => (await terms.GetTermsAsync(tenantId, ids, ct)).ToDictionary(t => t.Id, t => t.Name),
            "person" => await users.GetUserNamesAsync(tenantId, ids, ct),
            _ => new Dictionary<Guid, string>(),
        };
        return values.Where(v => Guid.TryParse(v, out var id) && names.ContainsKey(id)).ToDictionary(v => v, v => names[Guid.Parse(v)]);
    }
}
