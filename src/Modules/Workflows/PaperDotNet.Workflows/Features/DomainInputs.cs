using System.Text.Json.Nodes;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>Domain selections remain plain IDs in execution inputs; their constraints live in the schema.</summary>
internal static class DomainInputs
{
    /// <summary>
    /// The options of each kind besides <c>kind</c>. <c>groupId</c> is a taxonomy group (terms and keywords) and
    /// <c>memberOf</c> an identity group (people), so one key never means both.
    /// </summary>
    private static string[] KeysOf(string kind) => kind switch
    {
        "relationship" => ["relationshipType"],
        "people" => ["memberOf", "people", "groups"],
        _ => ["groupId", "termSetId", "termIds"],
    };

    /// <summary>
    /// Removes a deleted user or group from the people inputs anywhere in <paramref name="node"/> (a definition or a
    /// schema): a <c>memberOf</c> limit to it and its id in defaults. Returns whether anything changed.
    /// </summary>
    public static bool ForgetPrincipal(JsonNode? node, Guid principal)
    {
        bool Is(JsonNode? value) => value is JsonValue text && Guid.TryParse(text.ToString(), out var id) && id == principal;
        var changed = false;
        if (node is JsonObject schema)
        {
            if (schema["x-paperdotnet"] is JsonObject options && options["kind"]?.ToString() == "people")
            {
                if (Is(options["memberOf"]))
                {
                    options.Remove("memberOf");
                    changed = true;
                }

                if (Is(schema["default"]))
                {
                    schema.Remove("default");
                    changed = true;
                }
                else if (schema["default"] is JsonArray defaults && defaults.Where(Is).ToList() is { Count: > 0 } gone)
                {
                    gone.ForEach(value => defaults.Remove(value));
                    changed = true;
                }
            }

            foreach (var (_, child) in schema.ToList())
            {
                changed |= ForgetPrincipal(child, principal);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.ToList())
            {
                changed |= ForgetPrincipal(child, principal);
            }
        }

        return changed;
    }

    public static IEnumerable<string> Validate(JsonObject schema)
    {
        if (!schema.ContainsKey("x-paperdotnet"))
        {
            yield break;
        }

        if (schema["x-paperdotnet"] is not JsonObject options || options["kind"]?.ToString() is not ("relationship" or "terms" or "keywords" or "people"))
        {
            yield return "inputs.x-paperdotnet needs a kind: relationship, terms, keywords or people.";
            yield break;
        }

        if (schema["type"]?.ToString() != "string" && !(schema["type"]?.ToString() == "array" && (schema["items"] as JsonObject)?["type"]?.ToString() == "string"))
        {
            yield return "Domain selections must be strings or arrays of strings.";
        }

        var kind = options["kind"]!.ToString();
        if (kind == "relationship" && string.IsNullOrWhiteSpace(options["relationshipType"]?.ToString()))
        {
            yield return "Relationship selections require relationshipType (type ID or name).";
        }

        foreach (var key in options.Select(o => o.Key).Where(key => key != "kind" && !KeysOf(kind).Contains(key)))
        {
            yield return $"inputs.x-paperdotnet.{key} does not apply to {kind} selections.";
        }

        foreach (var key in new[] { "groupId", "termSetId", "memberOf" })
        {
            if (options.ContainsKey(key) && !Guid.TryParse(options[key]?.ToString(), out _))
            {
                yield return $"inputs.x-paperdotnet.{key} must be a GUID.";
            }
        }

        if (kind == "people")
        {
            foreach (var key in new[] { "people", "groups" })
            {
                if (options[key] is { } enabled && enabled.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                {
                    yield return $"inputs.x-paperdotnet.{key} must be a boolean.";
                }
            }

            if (options["people"]?.ToString() == "false" && options["groups"]?.ToString() == "false")
            {
                yield return "People selections must allow people, groups or both.";
            }
        }

        if (options.ContainsKey("termIds") && (options["termIds"] is not JsonArray { Count: > 0 } ids || ids.Any(id => !Guid.TryParse(id?.ToString(), out _))))
        {
            yield return "inputs.x-paperdotnet.termIds must be a nonempty array of term IDs.";
        }
    }

    public static async Task<string?> ValidateReferencesAsync(JsonObject? schema, IListItemStore items, ITermStore terms, IUserDirectory users, CancellationToken ct)
    {
        if (schema is null)
        {
            return null;
        }

        if (schema["x-paperdotnet"] is JsonObject options)
        {
            if (options["kind"]?.ToString() == "people" && options["memberOf"] is { } memberOf
                && !await users.GroupExistsAsync(Guid.Parse(memberOf.ToString()), ct))
            {
                return "The configured people group (memberOf) does not exist.";
            }

            if (options["termSetId"] is { } setId)
            {
                var set = await terms.GetTermSetAsync(Guid.Parse(setId.ToString()), ct);
                if (set is null || options["groupId"] is { } groupId && set.GroupId != Guid.Parse(groupId.ToString()))
                {
                    return "The configured term set does not exist in the configured group.";
                }
            }

            if (options["kind"]?.ToString() != "people" && await CheckAsync(schema, options["termIds"] ?? new JsonArray(), items, terms, users, ct) is { } error)
            {
                return error;
            }
        }

        foreach (var (_, child) in schema["properties"] as JsonObject ?? [])
        {
            if (await ValidateReferencesAsync(child as JsonObject, items, terms, users, ct) is { } error)
            {
                return error;
            }
        }

        return await ValidateReferencesAsync(schema["items"] as JsonObject, items, terms, users, ct);
    }

    public static async Task<string?> CheckAsync(JsonObject? schema, JsonNode? value, IListItemStore items, ITermStore terms, IUserDirectory users, CancellationToken ct)
    {
        if (schema is null || value is null)
        {
            return null;
        }

        if (schema["x-paperdotnet"] is JsonObject options)
        {
            var selected = value is JsonArray array ? array.Select(v => v?.ToString()).ToArray() : [value.ToString()];
            if (selected.Length > 100 || selected.Any(v => !Guid.TryParse(v, out var id) || id == Guid.Empty))
            {
                return "Domain selections require at most 100 distinct, valid IDs.";
            }

            var ids = selected.Select(v => Guid.Parse(v!)).ToArray();
            if (ids.Distinct().Count() != ids.Length)
            {
                return "Domain selections require distinct IDs.";
            }
            if (options["kind"]?.ToString() == "people")
            {
                // One query for the people, one for the groups among the rest; membership is part of the people query.
                var remaining = ids.ToHashSet();
                if (options["people"]?.ToString() != "false")
                {
                    var memberOf = options["memberOf"] is { } group ? Guid.Parse(group.ToString()) : (Guid?)null;
                    remaining.ExceptWith(await users.GetAssignableUsersAsync(ids, memberOf, ct));
                }

                if (remaining.Count > 0 && options["groups"]?.ToString() != "false")
                {
                    remaining.ExceptWith((await users.GetGroupNamesAsync(remaining, ct)).Keys);
                }

                if (remaining.Count > 0)
                {
                    return "A selected person or group is unavailable or outside the configured scope.";
                }
            }
            else if (options["kind"]?.ToString() == "relationship")
            {
                var type = options["relationshipType"]!.ToString();
                var types = await items.GetRelationshipTypesAsync(ct);
                if (!types.Any(t => t.Id.ToString().Equals(type, StringComparison.OrdinalIgnoreCase) || t.Name.Equals(type, StringComparison.OrdinalIgnoreCase)))
                {
                    return "The configured relationship type does not exist.";
                }

                foreach (var id in ids)
                {
                    if (await items.GetByIdAsync(id, ct) is null)
                    {
                        return "A selected relationship target is unavailable.";
                    }
                }
            }
            else
            {
                var found = await terms.GetTermsAsync(ids, ct);
                if (found.Count != ids.Length || found.Any(t => t.IsDeprecated || options["kind"]?.ToString() == "keywords" && !t.IsKeyword))
                {
                    return "A selected term is unavailable or is not an assignable keyword.";
                }

                var sets = new Dictionary<Guid, TermSetInfo?>();
                foreach (var term in found)
                {
                    if (options["groupId"] is not null && !sets.ContainsKey(term.TermSetId))
                    {
                        sets[term.TermSetId] = await terms.GetTermSetAsync(term.TermSetId, ct);
                    }

                    if (options["termIds"] is JsonArray allowed && !allowed.Any(id => Guid.Parse(id!.ToString()) == term.Id)
                        || options["termSetId"] is { } setId && Guid.Parse(setId.ToString()) != term.TermSetId
                        || options["groupId"] is { } groupId && sets[term.TermSetId]?.GroupId != Guid.Parse(groupId.ToString()))
                    {
                        return "A selected term is outside the configured scope.";
                    }
                }
            }
        }

        if (value is JsonObject obj && schema["properties"] is JsonObject properties)
        {
            foreach (var (name, child) in properties)
            {
                if (await CheckAsync(child as JsonObject, obj[name], items, terms, users, ct) is { } error)
                {
                    return $"{name}: {error}";
                }
            }
        }

        if (value is JsonArray values && schema["items"] is JsonObject itemSchema)
        {
            foreach (var child in values)
            {
                if (await CheckAsync(itemSchema, child, items, terms, users, ct) is { } error)
                {
                    return error;
                }
            }
        }

        return null;
    }
}
