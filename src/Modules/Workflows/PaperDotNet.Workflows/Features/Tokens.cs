using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperDotNet.Workflows.Features;

/// <summary>An item as tokens and scripts see it: its values (title included), id and dates.</summary>
internal sealed record ScopeItem(Guid Id, Guid ListId, JsonObject Fields, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>What tokens can refer to: the item, its list, the outputs of nodes that ran, variables and trigger data.</summary>
internal sealed record TokenScope(ScopeItem? Item, string? ListName, JsonObject? Outputs, JsonObject? Variables, JsonObject? Data)
{
    public JsonNode? Step(string reference) => Path(Outputs, reference);

    public JsonNode? Variable(string reference) => Path(Variables, reference);

    public JsonNode? Trigger(string reference) => Path(Data, reference);

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
/// Replaces <c>{token}</c> and <c>{token:format}</c> in inputs: item values (<c>{title}</c>, <c>{amount}</c>,
/// <c>{item:name}</c>), <c>{id}</c>, <c>{list}</c>, <c>{created:format}</c>, <c>{modified}</c>, <c>{today:format}</c>,
/// <c>{var:name}</c>, <c>{step:node.path}</c> and <c>{data:name}</c>. <c>{{</c> and <c>}}</c> are braces; unknown
/// tokens become empty text. Ported from the .NET 10 module without term and user names (ADR-0039).
/// </summary>
public sealed class TokenExpander(TimeProvider time)
{
    internal string Expand(string template, TokenScope scope)
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
            result.Append(Resolve(name, format, scope));
            i = end;
        }

        return result.ToString();
    }

    /// <summary>A text that is exactly one token without a format gives the value it refers to as JSON; other text is expanded.</summary>
    internal JsonNode? Value(string template, TokenScope scope)
    {
        if (template.Length > 2 && template[0] == '{' && template[1] != '{' && template.IndexOf('}', StringComparison.Ordinal) == template.Length - 1)
        {
            var token = template[1..^1];
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            var (name, reference) = colon < 0 ? (token.Trim(), null) : (token[..colon].Trim(), token[(colon + 1)..].Trim());
            static JsonNode? Json(JsonNode? value) => value is null ? null : JsonNode.Parse(value.ToJsonString());
            switch (name, reference)
            {
                case ("step", { } path):
                    return Json(scope.Step(path));
                case ("var", { } path):
                    return Json(scope.Variable(path));
                case ("data", { } path):
                    return Json(scope.Trigger(path));
                case ("item", { } field):
                    return Json(scope.Item?.Fields[field]);
                case (not ("id" or "list" or "created" or "modified" or "today"), null):
                    return Json(scope.Item?.Fields[name]);
            }
        }

        return JsonValue.Create(Expand(template, scope));
    }

    private string Resolve(string name, string? format, TokenScope scope)
    {
        var item = scope.Item;
        return name switch
        {
            "id" => item?.Id.ToString() ?? "",
            "list" => scope.ListName ?? "",
            "created" => item is null ? "" : Format(item.CreatedAt, format),
            "modified" => item is null ? "" : Format(item.UpdatedAt, format),
            "today" => Format(time.GetUtcNow(), format),
            "data" => format is not null && scope.Trigger(format.Trim()) is { } data ? Text(data, null) : "",
            "var" => format is not null && scope.Variable(format.Trim()) is { } variable ? Text(variable, null) : "",
            "step" => format is not null && scope.Step(format.Trim()) is { } output ? Text(output, null) : "",
            "item" => format?.Split(':', 2) is { Length: > 0 } field && item?.Fields[field[0].Trim()] is { } value
                ? Text(value, field.Length > 1 ? field[1] : null)
                : "",
            _ => item?.Fields[name] is { } fieldValue ? Text(fieldValue, format) : "",
        };
    }

    private static string Text(JsonNode value, string? format)
    {
        if (value is JsonArray array)
        {
            return string.Join(", ", array.OfType<JsonNode>().Select(e => Text(e, format)));
        }

        if (value is not JsonValue scalar)
        {
            return value.ToJsonString();
        }

        if (scalar.GetValueKind() == JsonValueKind.Number)
        {
            return decimal.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number.ToString(format, CultureInfo.InvariantCulture)
                : scalar.ToJsonString();
        }

        if (scalar.GetValueKind() != JsonValueKind.String)
        {
            return scalar.ToJsonString();
        }

        var text = scalar.GetValue<string>();
        return format is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? Format(date, format)
            : text;
    }

    private static string Format(DateTimeOffset value, string? format) =>
        value.UtcDateTime.ToString(string.IsNullOrWhiteSpace(format) ? "yyyy-MM-dd" : format, CultureInfo.InvariantCulture);
}
