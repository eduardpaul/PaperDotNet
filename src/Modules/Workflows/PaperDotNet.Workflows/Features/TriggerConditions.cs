using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>Bounded, typed condition trees evaluated over the write's snapshots, without item queries.</summary>
internal static class TriggerConditions
{
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "eq", "neq", "gt", "gte", "lt", "lte", "contains", "startsWith", "endsWith", "isEmpty", "isNotEmpty",
        "containsAny", "containsAll", "added", "removed", "changed", "transition",
    };
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "all", "any", "target", "field", "operator", "value", "from", "to", "term", "includeDescendants",
    };

    public static IEnumerable<string> Validate(WorkflowTrigger trigger)
    {
        if (trigger.Parameters is null) return [];
        var errors = new List<string>();
        if (trigger.Type is not (WorkflowTriggers.ItemAdded or WorkflowTriggers.ItemUpdated))
        {
            errors.Add("parameters.when is only supported on itemAdded and itemUpdated.");
        }
        var nodes = 0;
        Visit(trigger.Parameters.When, 1);
        return errors;

        void Visit(JsonObject? node, int depth)
        {
            if (++nodes > 100 || depth > 8)
            {
                errors.Add("parameters.when supports at most 100 nodes and eight levels.");
                return;
            }
            if (node is null || node.Count == 0 || node.Any(pair => !Keys.Contains(pair.Key)))
            {
                errors.Add("parameters.when contains an empty condition or unknown property.");
                return;
            }
            if (node.ContainsKey("all") || node.ContainsKey("any"))
            {
                var key = node.ContainsKey("all") ? "all" : "any";
                if (node.Count != 1 || node[key] is not JsonArray { Count: > 0 and <= 100 } children)
                {
                    errors.Add("Condition groups require exactly one nonempty all or any array (at most 100 nodes).");
                    return;
                }
                foreach (var child in children) Visit(child as JsonObject, depth + 1);
                return;
            }
            var target = Text(node, "target");
            var op = Text(node, "operator");
            if (target is not ("field" or "tags") || op is null || !Operators.Contains(op))
            {
                errors.Add("Conditions need target (field or tags) and a supported operator.");
                return;
            }
            if (trigger.Type == WorkflowTriggers.ItemAdded && op is "changed" or "removed" or "transition")
            {
                errors.Add($"{op} is update-only.");
            }
            if (target == "tags")
            {
                if (op is not ("contains" or "added" or "removed" or "isEmpty" or "isNotEmpty")
                    || node.ContainsKey("field") || node.ContainsKey("value") || node.ContainsKey("from") || node.ContainsKey("to"))
                {
                    errors.Add("Tag conditions use contains, added, removed, isEmpty or isNotEmpty, with an optional term path.");
                }
                if (op == "contains" && string.IsNullOrWhiteSpace(Text(node, "term"))) errors.Add("Tag contains requires a term path.");
                if (node["term"] is not null && (string.IsNullOrWhiteSpace(Text(node, "term")) || op is "isEmpty" or "isNotEmpty")) errors.Add("term must be a nonempty path on a contains, added or removed condition.");
                if (node.ContainsKey("includeDescendants") && node["includeDescendants"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) errors.Add("includeDescendants must be a boolean.");
                return;
            }
            if (string.IsNullOrWhiteSpace(Text(node, "field")) || node.ContainsKey("term") || node.ContainsKey("includeDescendants")) errors.Add("Field conditions require a field name and cannot specify tag options.");
            if (op == "transition")
            {
                if (!node.ContainsKey("from") || !node.ContainsKey("to") || node.ContainsKey("value")) errors.Add("transition requires from and to, without value.");
            }
            else
            {
                if (node.ContainsKey("from") || node.ContainsKey("to")) errors.Add("from and to are only supported by transition.");
                if (op is "changed" or "isEmpty" or "isNotEmpty")
                {
                    if (node.ContainsKey("value")) errors.Add($"{op} does not accept value.");
                }
                else if (op is not ("added" or "removed") && !node.ContainsKey("value")) errors.Add($"{op} requires value.");
            }
            if (op is "startsWith" or "endsWith" && node["value"]?.GetValueKind() != JsonValueKind.String) errors.Add($"{op} requires a string value.");
            if (op is "contains" or "added" or "removed" && node["value"] is JsonArray) errors.Add($"{op} requires a single primitive value.");
            if (op is "containsAny" or "containsAll" && node["value"] is not JsonArray { Count: > 0 and <= 100 }) errors.Add($"{op} requires a nonempty value array of at most 100 values.");
            foreach (var key in new[] { "value", "from", "to" })
            {
                if (node[key] is JsonObject || node[key] is JsonArray values && (values.Count > 100 || values.Any(value => value is JsonArray or JsonObject))) errors.Add("Comparison values must be primitives or arrays of at most 100 primitives.");
            }
        }
    }

    public static IEnumerable<JsonObject> Leaves(JsonObject? node)
    {
        if (node is null) yield break;
        if ((node["all"] ?? node["any"]) is JsonArray group)
        {
            foreach (var child in group.OfType<JsonObject>())
                foreach (var leaf in Leaves(child)) yield return leaf;
        }
        else yield return node;
    }

    public static string? ValidateField(JsonObject leaf, ItemSnapshotField definition)
    {
        var op = Text(leaf, "operator")!;
        if (!Supports(definition, op)) return $"Operator '{op}' is incompatible with field '{Text(leaf, "field")}' ({definition.Type}).";
        foreach (var key in op == "transition" ? new[] { "from", "to" } : new[] { "value" })
        {
            if (!leaf.ContainsKey(key)) continue;
            var value = leaf[key];
            var fits = op is "containsAny" or "containsAll" ? value is JsonArray array && array.All(v => Fits(definition with { AllowMultiple = false }, v))
                : op is "contains" or "added" or "removed" && definition.AllowMultiple ? Fits(definition with { AllowMultiple = false }, value)
                : Fits(definition, value);
            if (!fits) return $"Operand '{key}' is incompatible with field '{Text(leaf, "field")}' ({definition.Type}).";
        }
        return null;
    }

    private static bool Supports(ItemSnapshotField field, string op) => op switch
    {
        "containsAny" or "containsAll" or "added" or "removed" => field.AllowMultiple,
        "startsWith" or "endsWith" => !field.AllowMultiple && IsText(field.Type),
        "contains" => field.AllowMultiple || IsText(field.Type),
        "gt" or "gte" or "lt" or "lte" => !field.AllowMultiple && (IsText(field.Type) || field.Type is "number" or "currency" or "date" or "dateTime"),
        _ => true,
    };

    private static bool IsText(string type) => type is "text" or "note" or "email" or "url" or "choice";

    private static bool Fits(ItemSnapshotField field, JsonNode? value)
    {
        if (value is null) return true;
        if (field.AllowMultiple) return value is JsonArray array && array.Count <= 100 && array.All(v => Fits(field with { AllowMultiple = false }, v));
        return field.Type switch
        {
            "number" or "currency" => value.GetValueKind() == JsonValueKind.Number && decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            "boolean" => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "date" => value.GetValueKind() == JsonValueKind.String && DateOnly.TryParseExact(value.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "dateTime" => value.GetValueKind() == JsonValueKind.String && DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
            "person" or "lookup" or "managedMetadata" or "keywords" => value.GetValueKind() == JsonValueKind.String && Guid.TryParse(value.ToString(), out _),
            _ => IsText(field.Type) && value.GetValueKind() == JsonValueKind.String,
        };
    }

    public static async Task<bool> MatchesAsync(JsonObject node, ItemEvent source, ITermStore terms, Dictionary<string, HashSet<Guid>> termCache, CancellationToken ct)
    {
        if (source.After is not { } after || source is ItemUpdated && source.Before is null) return false;
        if (node["all"] is JsonArray all)
        {
            foreach (var child in all.OfType<JsonObject>()) if (!await MatchesAsync(child, source, terms, termCache, ct)) return false;
            return true;
        }
        if (node["any"] is JsonArray any)
        {
            foreach (var child in any.OfType<JsonObject>()) if (await MatchesAsync(child, source, terms, termCache, ct)) return true;
            return false;
        }
        var op = Text(node, "operator")!;
        if (Text(node, "target") == "tags")
        {
            if (!after.FieldTypes.Values.Any(field => field.Type is "managedMetadata" or "keywords")) return false;
            var current = Tags(after);
            var previous = Tags(source.Before);
            var values = op switch { "added" => current.Except(previous).ToHashSet(), "removed" => previous.Except(current).ToHashSet(), _ => current };
            if (op == "isEmpty") return values.Count == 0;
            if (op == "isNotEmpty") return values.Count != 0;
            if (Text(node, "term") is not { } path) return values.Count > 0;
            var descendants = node["includeDescendants"]?.GetValue<bool>() ?? true;
            var key = $"condition:{descendants}:{path}";
            if (!termCache.TryGetValue(key, out var wanted))
            {
                wanted = [];
                if (await terms.FindTermByPathAsync(path, ct) is { } id)
                {
                    wanted.Add(id);
                    if (descendants)
                        foreach (var children in (await terms.GetDescendantsAsync([id], ct)).Values) wanted.UnionWith(children);
                }
                termCache[key] = wanted;
            }
            return values.Overlaps(wanted);
        }
        var name = Text(node, "field")!;
        if (!after.FieldTypes.TryGetValue(name, out var type) || ValidateField(node, type) is not null) return false;
        var value = after.Fields[name];
        var old = source.Before?.Fields[name];
        if (!FitsStored(type, value)) return false;
        if (op is "changed" or "transition" or "added" or "removed")
        {
            if (source.Before is { } before && (!before.FieldTypes.TryGetValue(name, out var oldType) || oldType != type || !FitsStored(type, old))) return false;
            if (op == "changed") return !Equal(type, old, value);
            if (op == "transition") return Equal(type, old, node["from"]) && Equal(type, value, node["to"]);
            var previous = ValueSet(type.Type, old);
            var current = ValueSet(type.Type, value);
            var difference = op == "added" ? current : previous;
            difference.ExceptWith(op == "added" ? previous : current);
            return node.ContainsKey("value") ? difference.Contains(ValueKey(type.Type, node["value"])) : difference.Count > 0;
        }
        if (op == "isEmpty") return Empty(value);
        if (op == "isNotEmpty") return !Empty(value);
        if (op is "eq" or "neq") return Equal(type, value, node["value"]) == (op == "eq");
        if (op is "containsAny" or "containsAll")
        {
            var wanted = (JsonArray)node["value"]!;
            var values = ValueSet(type.Type, value);
            bool Contains(JsonNode? operand) => values.Contains(ValueKey(type.Type, operand));
            return op == "containsAny" ? wanted.Any(Contains) : wanted.All(Contains);
        }
        if (op == "contains" && type.AllowMultiple) return Elements(value).Any(v => Equal(type with { AllowMultiple = false }, v, node["value"]));
        if (value is null || node["value"] is null) return false;
        if (op is "contains" or "startsWith" or "endsWith")
        {
            var text = value.ToString();
            var operand = node["value"]!.ToString();
            return op switch { "contains" => text.Contains(operand, StringComparison.Ordinal), "startsWith" => text.StartsWith(operand, StringComparison.Ordinal), _ => text.EndsWith(operand, StringComparison.Ordinal) };
        }
        var comparison = Compare(type.Type, value, node["value"]!);
        return op switch { "gt" => comparison > 0, "gte" => comparison >= 0, "lt" => comparison < 0, "lte" => comparison <= 0, _ => false };
    }

    private static bool FitsStored(ItemSnapshotField type, JsonNode? value) => type.AllowMultiple && value is JsonArray array
        ? array.All(v => Fits(type with { AllowMultiple = false }, v)) : Fits(type, value);
    private static bool Empty(JsonNode? value) => value is null || value is JsonArray { Count: 0 } || value.GetValueKind() == JsonValueKind.String && value.ToString().Length == 0;
    private static JsonArray Elements(JsonNode? value) => value is JsonArray array ? array : [];
    private static bool Equal(ItemSnapshotField type, JsonNode? left, JsonNode? right)
    {
        if (type.AllowMultiple) return ValueSet(type.Type, left).SetEquals(ValueSet(type.Type, right));
        if (left is null || right is null) return left is null && right is null;
        if (type.Type is "number" or "currency" or "date" or "dateTime") return Compare(type.Type, left, right) == 0;
        if (type.Type is "person" or "lookup" or "managedMetadata" or "keywords") return Guid.Parse(left.ToString()) == Guid.Parse(right.ToString());
        return JsonNode.DeepEquals(left, right);
    }
    private static HashSet<string> ValueSet(string type, JsonNode? value) => Elements(value).Select(v => ValueKey(type, v)).ToHashSet(StringComparer.Ordinal);
    private static string ValueKey(string type, JsonNode? value) => value is null ? "null" : type switch
    {
        "number" or "currency" => "number:" + decimal.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture),
        "date" => "date:" + DateOnly.Parse(value.ToString(), CultureInfo.InvariantCulture).DayNumber.ToString(CultureInfo.InvariantCulture),
        "dateTime" => "dateTime:" + DateTimeOffset.Parse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
        "person" or "lookup" or "managedMetadata" or "keywords" => "id:" + Guid.Parse(value.ToString()).ToString("N"),
        _ => "value:" + value.ToString(),
    };
    private static int Compare(string type, JsonNode left, JsonNode right) => type switch
    {
        "number" or "currency" => decimal.Parse(left.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(right.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)),
        "date" => DateOnly.Parse(left.ToString(), CultureInfo.InvariantCulture).CompareTo(DateOnly.Parse(right.ToString(), CultureInfo.InvariantCulture)),
        "dateTime" => DateTimeOffset.Parse(left.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).CompareTo(DateTimeOffset.Parse(right.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)),
        _ => string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal),
    };
    private static HashSet<Guid> Tags(ItemSnapshot? snapshot) => snapshot is null ? [] : snapshot.FieldTypes
        .Where(pair => pair.Value.Type is "managedMetadata" or "keywords")
        .SelectMany(pair => snapshot.Fields[pair.Key] is JsonArray array ? array.AsEnumerable() : [snapshot.Fields[pair.Key]])
        .OfType<JsonValue>().Select(value => Guid.TryParse(value.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
    private static string? Text(JsonObject node, string key) => node[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
