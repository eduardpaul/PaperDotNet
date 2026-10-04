using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>Domain selections remain plain IDs in execution inputs; their constraints live in the schema.</summary>
internal static class DomainInputs
{
    public static IEnumerable<string> Validate(JsonObject schema)
    {
        if (!schema.ContainsKey("x-paperdotnet"))
        {
            yield break;
        }

        if (schema["x-paperdotnet"] is not JsonObject options || options["kind"]?.ToString() is not ("relationship" or "terms" or "keywords"))
        {
            yield return "inputs.x-paperdotnet needs a kind: relationship, terms or keywords.";
            yield break;
        }

        if (schema["type"]?.ToString() != "string" && !(schema["type"]?.ToString() == "array" && (schema["items"] as JsonObject)?["type"]?.ToString() == "string"))
        {
            yield return "Domain selections must be strings or arrays of strings.";
        }

        if (options["kind"]?.ToString() == "relationship" && string.IsNullOrWhiteSpace(options["relationshipType"]?.ToString()))
        {
            yield return "Relationship selections require relationshipType (type ID or name).";
        }

        foreach (var key in new[] { "groupId", "termSetId" })
        {
            if (options.ContainsKey(key) && !Guid.TryParse(options[key]?.ToString(), out _))
            {
                yield return $"inputs.x-paperdotnet.{key} must be a GUID.";
            }
        }

        if (options.ContainsKey("termIds") && (options["termIds"] is not JsonArray { Count: > 0 } ids || ids.Any(id => !Guid.TryParse(id?.ToString(), out _))))
        {
            yield return "inputs.x-paperdotnet.termIds must be a nonempty array of term IDs.";
        }
    }

    public static async Task<string?> ValidateReferencesAsync(JsonObject? schema, IListItemStore items, ITermStore terms, CancellationToken ct)
    {
        if (schema is null)
        {
            return null;
        }

        if (schema["x-paperdotnet"] is JsonObject options)
        {
            if (options["termSetId"] is { } setId)
            {
                var set = await terms.GetTermSetAsync(Guid.Parse(setId.ToString()), ct);
                if (set is null || options["groupId"] is { } groupId && set.GroupId != Guid.Parse(groupId.ToString()))
                {
                    return "The configured term set does not exist in the configured group.";
                }
            }

            if (await CheckAsync(schema, options["termIds"] ?? new JsonArray(), items, terms, ct) is { } error)
            {
                return error;
            }
        }

        foreach (var (_, child) in schema["properties"] as JsonObject ?? [])
        {
            if (await ValidateReferencesAsync(child as JsonObject, items, terms, ct) is { } error)
            {
                return error;
            }
        }

        return await ValidateReferencesAsync(schema["items"] as JsonObject, items, terms, ct);
    }

    public static async Task<string?> CheckAsync(JsonObject? schema, JsonNode? value, IListItemStore items, ITermStore terms, CancellationToken ct)
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
            if (options["kind"]?.ToString() == "relationship")
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
                if (await CheckAsync(child as JsonObject, obj[name], items, terms, ct) is { } error)
                {
                    return $"{name}: {error}";
                }
            }
        }

        if (value is JsonArray values && schema["items"] is JsonObject itemSchema)
        {
            foreach (var child in values)
            {
                if (await CheckAsync(itemSchema, child, items, terms, ct) is { } error)
                {
                    return error;
                }
            }
        }

        return null;
    }
}
