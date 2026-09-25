using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Automation.Features;

/// <summary>What tokens can refer to: the item, its list, approval outcomes and trigger data.</summary>
internal sealed record TokenScope(ListItemData? Item, string? ListName, IReadOnlyDictionary<string, string> Outcomes, JsonObject? Data)
{
    public static readonly TokenScope Empty = new(null, null, new Dictionary<string, string>(), null);
}

/// <summary>
/// Replaces <c>{token}</c> and <c>{token:format}</c> in action inputs (see <c>AutomationActionContext.ExpandAsync</c>).
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
                return format is not null && scope.Outcomes.TryGetValue(format.Trim(), out var outcome) ? outcome : string.Empty;
            case "data":
                return format is not null && scope.Data?[format.Trim()] is { } data ? await ValueAsync(data, null, ct) : string.Empty;
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
            var number = scalar.GetValue<decimal>();
            return number.ToString(format, CultureInfo.InvariantCulture);
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
/// (the user whose change started the automation).
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
