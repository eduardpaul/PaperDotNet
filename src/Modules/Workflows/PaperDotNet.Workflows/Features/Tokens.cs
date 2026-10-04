using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>What tokens can refer to: the item, its list, the outputs of nodes that ran, variables and trigger data.</summary>
internal sealed record TokenScope(ListItemData? Item, string? ListName, JsonObject? Outputs, JsonObject? Variables, JsonObject? Data, JsonObject? Context = null)
{
    public static readonly TokenScope Empty = new(null, null, null, null, null);

    /// <summary>The scope of a run's node: the item as it is now (and its list's name) with the run's state.</summary>
    public static async Task<TokenScope> LoadAsync(
        IListItemStore items, WorkflowItem? item, JsonObject? outputs, JsonObject? variables, JsonObject? data, CancellationToken ct, JsonObject? context = null)
    {
        var store = items.AsSystem();
        var current = item is null ? null : await store.GetAsync(item.WorkspaceId, item.ListId, item.ItemId, ct);
        var list = item is null ? null : await store.GetListAsync(item.WorkspaceId, item.ListId, ct);
        return new TokenScope(current, list?.Name, outputs, variables, data, context);
    }

    /// <summary>
    /// A value in a node's output: <c>Node.path.to.value</c>. Node ids may contain dots, so the longest id that is an
    /// output wins.
    /// </summary>
    public JsonNode? Step(string reference) => Path(Outputs, reference);

    /// <summary>A variable, or a value in it: <c>line.description</c>.</summary>
    public JsonNode? Variable(string reference) => Path(Variables, reference);

    /// <summary>A value of the trigger data, or in it.</summary>
    public JsonNode? Trigger(string reference) => Path(Data, reference);

    public JsonNode? Execution(string reference) => Path(Context, reference);

    public JsonNode? Input(string reference) => Path(Context?["input"] as JsonObject, reference);

    /// <summary>A value under a name (the longest name that exists wins, as names may contain dots) and a path into it.</summary>
    private static JsonNode? Path(JsonObject? root, string reference)
    {
        if (root is null)
        {
            return null;
        }

        for (var split = reference.Length; split > 0; split = reference.LastIndexOf('.', split - 1))
        {
            if (root[reference[..split]] is { } output)
            {
                JsonNode? value = output;
                foreach (var part in split < reference.Length ? reference[(split + 1)..].Split('.') : [])
                {
                    value = value switch
                    {
                        JsonObject obj => obj[part],
                        JsonArray array when int.TryParse(part, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < array.Count => array[index],
                        _ => null,
                    };
                }

                return value;
            }
        }

        return null;
    }
}

/// <summary>
/// Replaces <c>{token}</c> and <c>{token:format}</c> in action inputs (see <c>WorkflowActivityContext.ExpandAsync</c>):
/// item fields, <c>{outcome:Node}</c>, <c>{var:name}</c>, <c>{step:Node.path}</c> and <c>{trigger:name}</c> (or <c>{data:name}</c>).
/// Term ids become their names and user ids user names; unknown tokens become empty text.
/// </summary>
internal sealed class TokenExpander(ITermStore terms, IUserDirectory users, TimeProvider time)
{
    public async Task<string> ExpandAsync(string template, TokenScope scope, CancellationToken ct)
    {
        var result = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                result.Append(c);
                i++;
                continue;
            }

            var end = c == '{' ? template.IndexOf('}', i + 1) : -1;
            if (end < 0)
            {
                result.Append(c);
                continue;
            }

            var token = template[(i + 1)..end];
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            var (name, format) = colon < 0 ? (token.Trim(), null) : (token[..colon].Trim(), token[(colon + 1)..]);
            result.Append(await ResolveAsync(name, format, scope, ct));
            i = end;
        }

        return result.ToString();
    }

    /// <summary>
    /// The value of an input (see <c>WorkflowActivityContext.ResolveAsync</c>): a text that is exactly one token without a
    /// format gives the value it refers to as JSON (<c>{step:…}</c>, <c>{var:…}</c>, <c>{trigger:…}</c> and item fields);
    /// other text is expanded. Null when the token has no value.
    /// </summary>
    public async Task<JsonNode?> ValueAsync(string template, TokenScope scope, CancellationToken ct)
    {
        if (template.Length > 2 && template[0] == '{' && template[1] != '{' && template.IndexOf('}', StringComparison.Ordinal) == template.Length - 1)
        {
            var token = template[1..^1];
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            var (name, reference) = colon < 0 ? (token.Trim(), null) : (token[..colon].Trim(), token[(colon + 1)..].Trim());
            // As plain JSON: an activity's output made in memory holds .NET values (an int count) that field types do not read.
            static JsonNode? Json(JsonNode? value) => value is null ? null : JsonNode.Parse(value.ToJsonString());
            switch (name, reference)
            {
                case ("context", { } path):
                    return Json(scope.Execution(path));
                case ("input", { } path):
                    return Json(scope.Input(path));
                case ("step", { } path):
                    return Json(scope.Step(path));
                case ("var", { } path):
                    return Json(scope.Variable(path));
                case ("data" or "trigger", { } path):
                    return Json(scope.Trigger(path));
                case ("item", { } field) when !field.Contains(':', StringComparison.Ordinal):
                    return Json(scope.Item?.Fields[field]);
                case (not ("id" or "list" or "created" or "modified" or "today" or "outcome"), null):
                    return Json(scope.Item?.Fields[name]);
            }
        }

        return JsonValue.Create(await ExpandAsync(template, scope, ct));
    }

    private async Task<string> ResolveAsync(string name, string? format, TokenScope scope, CancellationToken ct)
    {
        var item = scope.Item;
        switch (name)
        {
            case "id":
                return item?.Id.ToString() ?? string.Empty;
            case "list":
                return scope.ListName ?? string.Empty;
            case "created":
                return item is null ? string.Empty : Format(item.CreatedAt, format);
            case "modified":
                return item is null ? string.Empty : Format(item.UpdatedAt, format);
            case "today":
                return Format(time.GetUtcNow(), format);
            case "outcome":
                return format is not null && scope.Outputs?[format.Trim()]?["outcome"] is JsonValue outcome ? outcome.ToString() : string.Empty;
            case "data" or "trigger":
                return format is not null && scope.Trigger(format.Trim()) is { } data ? await ValueAsync(data, null, ct) : string.Empty;
            case "var":
                return format is not null && scope.Variable(format.Trim()) is { } variable ? await ValueAsync(variable, null, ct) : string.Empty;
            case "context":
                return format is not null && scope.Execution(format.Trim()) is { } context ? await ValueAsync(context, null, ct) : string.Empty;
            case "input":
                return format is not null && scope.Input(format.Trim()) is { } input ? await ValueAsync(input, null, ct) : string.Empty;
            case "step":
                return format is not null && scope.Step(format.Trim()) is { } output ? await ValueAsync(output, null, ct) : string.Empty;
            case "item":
                // {item:name} is always the item's field, whatever it is called ({item:name:format} with a format).
                var field = format?.Split(':', 2);
                return field is { Length: > 0 } && item?.Fields[field[0].Trim()] is { } fieldValue
                    ? await ValueAsync(fieldValue, field.Length > 1 ? field[1] : null, ct)
                    : string.Empty;
        }

        return item?.Fields[name] is { } value ? await ValueAsync(value, format, ct) : string.Empty;
    }

    private async Task<string> ValueAsync(JsonNode value, string? format, CancellationToken ct)
    {
        if (value is JsonArray array)
        {
            var parts = new List<string>();
            foreach (var element in array.OfType<JsonNode>())
            {
                parts.Add(await ValueAsync(element, format, ct));
            }

            return string.Join(", ", parts);
        }

        if (value is not JsonValue scalar)
        {
            return value.ToJsonString();
        }

        if (scalar.GetValueKind() == JsonValueKind.Number)
        {
            // Read from the JSON text: an output made in memory holds an int or a double, which GetValue<decimal> refuses.
            return decimal.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number.ToString(format, CultureInfo.InvariantCulture)
                : scalar.ToJsonString();
        }

        if (scalar.GetValueKind() != JsonValueKind.String)
        {
            return scalar.ToJsonString();
        }

        var text = scalar.GetValue<string>();
        if (Guid.TryParse(text, out var id))
        {
            if ((await terms.GetLabelsAsync([id], ct)).TryGetValue(id, out var labels) && labels.Count > 0)
            {
                return labels[0];
            }

            if ((await users.GetUserNamesAsync([id], ct)).TryGetValue(id, out var userName))
            {
                return userName;
            }

            return text;
        }

        if (format is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return Format(date, format);
        }

        return text;
    }

    private static string Format(DateTimeOffset value, string? format) =>
        value.UtcDateTime.ToString(string.IsNullOrWhiteSpace(format) ? "yyyy-MM-dd" : format, CultureInfo.InvariantCulture);
}

/// <summary>
/// Resolves recipient and assignee specifications: user names, <c>group:Name</c> (its members),
/// <c>field:fieldName</c> (person values of the item), <c>creator</c> (of the item) and <c>actor</c>
/// (the user whose change started the workflow).
/// </summary>
internal sealed class RecipientResolver(IUserDirectory users)
{
    public async Task<(List<Guid> Users, List<string> Unknown)> ResolveAsync(
        IEnumerable<string> specs, ListItemData? item, Guid? actor, CancellationToken ct)
    {
        var result = new List<Guid>();
        var unknown = new List<string>();
        foreach (var spec in specs.Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (spec == "creator")
            {
                result.AddRange(item?.CreatedBy is { } creator ? [creator] : []);
            }
            else if (spec == "actor")
            {
                result.AddRange(actor is { } user ? [user] : []);
            }
            else if (spec.StartsWith("field:", StringComparison.Ordinal))
            {
                var value = item?.Fields[spec["field:".Length..]];
                var values = value is JsonArray array ? array.OfType<JsonNode>() : value is null ? [] : [value];
                result.AddRange(values.Select(v => v is JsonValue s && s.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty));
            }
            else if (spec.StartsWith("group:", StringComparison.Ordinal))
            {
                if (await users.FindGroupAsync(spec["group:".Length..], ct) is { } group)
                {
                    result.AddRange(await users.GetGroupMembersAsync(group, ct));
                }
                else
                {
                    unknown.Add(spec);
                }
            }
            else if (await users.FindUserAsync(spec, ct) is { } user)
            {
                result.Add(user);
            }
            else
            {
                unknown.Add(spec);
            }
        }

        return ([.. result.Distinct()], unknown);
    }
}
