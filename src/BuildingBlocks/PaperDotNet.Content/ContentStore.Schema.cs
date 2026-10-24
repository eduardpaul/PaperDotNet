using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Content;

public sealed partial class ContentStore
{
    private static readonly HashSet<string> FieldTypes = ["text", "note", "email", "url", "date", "lookup", "person", "term", "keywords", "choice", "datetime", "integer", "decimal", "number", "currency", "boolean"];

    private async Task<EffectiveSchema> SaveCatalogAsync(ResourcesRow collection, EffectiveSchema schema, CancellationToken ct)
    {
        if (schema.ContentTypes.Count == 0 || schema.ContentTypes.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != schema.ContentTypes.Count
            || schema.Fields.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != schema.Fields.Count
            || schema.ContentTypes.Count(x => x.IsDefault) != 1)
            throw new ArgumentException("Schema requires unique fields and content types, with exactly one default.", nameof(schema));
        var otherTypeIds = await db.Set<CollectionTypesRow>().Where(x => x.ContainerId != collection.Id)
            .Select(x => x.ContentTypeId).Distinct().ToArrayAsync(ct);
        var sharedTypes = await db.Set<ContentTypesRow>().AsNoTracking().Where(x => otherTypeIds.Contains(x.Id)).ToListAsync(ct);
        var sharedFields = sharedTypes.SelectMany(x => Decode<string[]>(x.FieldIds)).ToHashSet(StringComparer.Ordinal);
        var fields = new List<SchemaField>();
        foreach (var field in schema.Fields)
        {
            if (!FieldKey().IsMatch(field.Id) || !FieldTypes.Contains(field.Type) || string.IsNullOrWhiteSpace(field.Label)
                || field.Scale is < 0 or > 9 || field.Type != "decimal" && field.Scale != 0
                || field.Options?.Unique == true && field.Options.Multiple
                || field.Options?.ApplyParents == true && field.Type is not ("term" or "keywords"))
                throw new ArgumentException("Invalid field identity or options.", nameof(schema));
            var definitionId = field.DefinitionId ?? NewId();
            var definition = await db.Set<FieldDefinitionsRow>().SingleOrDefaultAsync(x => x.Id == definitionId, ct);
            if (definition is null)
            {
                definition = new FieldDefinitionsRow { Id = definitionId, CreatedAt = Now, Key = field.Id, Type = field.Type, Scale = field.Scale };
                db.Set<FieldDefinitionsRow>().Add(definition);
            }
            else
            {
                var old = new SchemaField(definition.Key, definition.Label, definition.Type, Scale: (int)definition.Scale,
                    DefinitionId: definition.Id, Options: Decode<SchemaFieldOptions>(definition.Options));
                var updated = field with { DefinitionId = definitionId, Options = field.Options ?? new SchemaFieldOptions() };
                if (!SameIdentity(old, updated)) throw new InvalidOperationException("Field identity is immutable; create a new definition.");
                var changed = definition.Label != field.Label || definition.Required != field.Required || definition.Indexed != field.Indexed
                    || !JsonElement.DeepEquals(Decode<JsonElement>(definition.Choices), JsonSerializer.SerializeToElement(field.Choices ?? []))
                    || Encode(Decode<SchemaFieldOptions>(definition.Options)) != Encode(updated.Options);
                if (changed && sharedFields.Contains(definitionId))
                    throw new InvalidOperationException("This field is shared with other collections. Use a new definition ID or a coordinated catalog migration.");
                if (changed) definition.Version++;
            }
            definition.Label = field.Label; definition.Required = field.Required; definition.Indexed = field.Indexed;
            definition.Choices = Encode(field.Choices ?? []); definition.Options = Encode(field.Options ?? new SchemaFieldOptions());
            fields.Add(field with { DefinitionId = definitionId, Choices = field.Choices ?? [], Options = field.Options ?? new SchemaFieldOptions() });
        }
        var byKey = fields.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var type in schema.ContentTypes)
        {
            if (string.IsNullOrWhiteSpace(type.Id) || !FieldKey().IsMatch(type.Key) || string.IsNullOrWhiteSpace(type.Name)
                || type.FieldKeys.Distinct(StringComparer.Ordinal).Count() != type.FieldKeys.Count || type.FieldKeys.Any(x => !byKey.ContainsKey(x)))
                throw new ArgumentException("Invalid content type.", nameof(schema));
            ValidateRules(type.FieldKeys.Select(x => byKey[x]).ToArray(), type.Rules ?? []);
            var row = await db.Set<ContentTypesRow>().SingleOrDefaultAsync(x => x.Id == type.Id, ct);
            if (row is null)
            {
                row = new ContentTypesRow { Id = type.Id, CreatedAt = Now, Key = type.Key };
                db.Set<ContentTypesRow>().Add(row);
            }
            else
            {
                if (row.Key != type.Key) throw new InvalidOperationException("Content type keys are immutable.");
                var changed = row.Name != type.Name
                    || !Decode<string[]>(row.FieldIds).SequenceEqual(type.FieldKeys.Select(x => byKey[x].DefinitionId), StringComparer.Ordinal)
                    || !JsonElement.DeepEquals(Decode<JsonElement>(row.Rules), JsonSerializer.SerializeToElement(type.Rules ?? [], Json));
                if (changed && otherTypeIds.Contains(type.Id, StringComparer.Ordinal))
                    throw new InvalidOperationException("This content type is shared with other collections. Use a new type ID or a coordinated catalog migration.");
                if (changed) row.Version++;
            }
            row.Name = type.Name; row.UpdatedAt = Now;
            row.FieldIds = Encode(type.FieldKeys.Select(x => byKey[x].DefinitionId).ToArray());
            row.Rules = Encode(type.Rules ?? []);
        }
        ValidateRules(fields, schema.Rules ?? []);
        var accepted = await db.Set<CollectionTypesRow>().Where(x => x.ContainerId == collection.Id).ToListAsync(ct);
        foreach (var row in accepted) row.IsDefault = false;
        db.Set<CollectionTypesRow>().RemoveRange(accepted.Where(x => schema.ContentTypes.All(t => t.Id != x.ContentTypeId)));
        await db.SaveChangesAsync(ct);
        foreach (var type in schema.ContentTypes)
        {
            var link = accepted.SingleOrDefault(x => x.ContentTypeId == type.Id);
            if (link is null)
            {
                link = new CollectionTypesRow { Id = NewId(), CreatedAt = Now, ContainerId = collection.Id, ContentTypeId = type.Id };
                db.Set<CollectionTypesRow>().Add(link);
            }
            link.IsDefault = type.IsDefault;
        }
        await db.SaveChangesAsync(ct);
        return schema with { Fields = fields };
    }

    private async Task<string> ValidateValuesAsync(EffectiveSchema schema, ContentInput input, CancellationToken ct,
        ItemRevisionsRow? previous = null, bool folder = false)
    {
        if (input.Values.ValueKind != JsonValueKind.Object || input.Values.GetRawText().Length > 8 * 1024 * 1024)
            throw new ArgumentException("Values must be a JSON object of at most 8 MiB.", nameof(input));
        var type = schema.ContentTypes.SingleOrDefault(x => x.Id == input.ContentTypeId)
            ?? throw new InvalidOperationException("Content type is not accepted by this schema.");
        var fields = schema.Fields.Where(x => type.FieldKeys.Contains(x.Id, StringComparer.Ordinal)).ToDictionary(x => x.Id, StringComparer.Ordinal);
        var values = Decode<Dictionary<string, JsonElement>>(input.Values.GetRawText());
        var old = previous is null || previous.ContentTypeId != input.ContentTypeId ? null : Decode<Dictionary<string, JsonElement>>(previous.Payload);
        if (values.Keys.Any(x => !fields.ContainsKey(x))) throw new ArgumentException("Values contain an unknown field.", nameof(input));
        foreach (var field in fields.Values)
        {
            var exists = values.TryGetValue(field.Id, out var value);
            if (old is not null)
            {
                var existed = old.TryGetValue(field.Id, out var before);
                if (exists == existed && (!exists || JsonElement.DeepEquals(before, value))) continue;
            }
            if (previous is null && !folder && !exists && field.Options?.DefaultValue is JsonElement fallback) { value = fallback; exists = true; }
            if (!folder && field.Required && (!exists || Empty(value))) throw new ArgumentException($"Required field missing: {field.Id}", nameof(input));
            if (!exists) continue;
            if (value.ValueKind == JsonValueKind.Null) { values[field.Id] = value; continue; }
            if (field.Options?.Multiple == true)
            {
                if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException($"{field.Id} requires an array.", nameof(input));
                var normalized = new List<JsonElement>();
                foreach (var entry in value.EnumerateArray()) normalized.Add(await NormalizeAsync(field, entry, ct));
                values[field.Id] = JsonSerializer.SerializeToElement(normalized, Json);
            }
            else values[field.Id] = await NormalizeAsync(field, value, ct);
        }
        if (!folder) EvaluateRules(fields, (schema.Rules ?? []).Concat(type.Rules ?? []), values);
        return Encode(values);
    }

    private async Task<JsonElement> NormalizeAsync(SchemaField field, JsonElement value, CancellationToken ct)
    {
        JsonElement normalized;
        switch (field.Type)
        {
            case "integer": normalized = JsonSerializer.SerializeToElement(value.GetInt64()); break;
            case "decimal": normalized = JsonSerializer.SerializeToElement(ScaledDecimal(value, field.Scale).Canonical); break;
            case "number": case "currency": normalized = JsonSerializer.SerializeToElement(value.GetDecimal()); break;
            case "boolean": normalized = JsonSerializer.SerializeToElement(value.GetBoolean()); break;
            default:
                var text = value.GetString() ?? throw new ArgumentException($"{field.Id} requires text.");
                var maximum = field.Options?.MaxLength ?? (field.Type switch { "text" or "email" => 255, "note" => 100000, "url" => 2048, _ => 0 });
                if (maximum > 0 && text.Length > maximum) throw new ArgumentException($"{field.Id} is too long.");
                switch (field.Type)
                {
                    case "choice" when !(field.Choices ?? []).Contains(text, StringComparer.Ordinal): throw new ArgumentException("Unknown choice.");
                    case "date" when !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _): throw new ArgumentException("Invalid date.");
                    case "datetime": text = NormalizeInstant(text); break;
                    case "email" when (!System.Net.Mail.MailAddress.TryCreate(text, out var address) || address.Address != text): throw new ArgumentException("Invalid email.");
                    case "url" when !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"): throw new ArgumentException("Invalid URL.");
                    case "person":
                        if (!await db.Set<IdentityUsersRow>().AnyAsync(x => x.Id == text && x.Active && x.DeletedAt == null, ct)) throw new ArgumentException("Unknown person.");
                        break;
                    case "lookup":
                        if (!await db.Resources.AnyAsync(x => x.Id == text && x.DeletedAt == null && x.Kind == "item" && x.ContainerId == field.Options!.LookupContainerId, ct)) throw new ArgumentException("Invalid lookup target.");
                        break;
                    case "term":
                    case "keywords":
                        if (!await db.Set<TermsRow>().AnyAsync(x => x.Id == text && !x.Deprecated && x.MergedIntoId == null && x.TermSetId == field.Options!.TermSetId, ct)) throw new ArgumentException("Invalid taxonomy term.");
                        break;
                }
                normalized = JsonSerializer.SerializeToElement(text);
                break;
        }
        if (field.Options?.Minimum is string minimum && Compare(field, normalized, Bound(field, minimum)) < 0
            || field.Options?.Maximum is string maximumValue && Compare(field, normalized, Bound(field, maximumValue)) > 0)
            throw new ArgumentException($"{field.Id} is outside its bounds.");
        return normalized;
    }

    private static JsonElement Bound(SchemaField field, string value) => field.Type is "integer" or "number" or "currency"
        ? JsonSerializer.Deserialize<JsonElement>(value) : JsonSerializer.SerializeToElement(value);

    private static (string Canonical, long Scaled) ScaledDecimal(JsonElement value, int scale)
    {
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        if (raw.Length > 80 || !DecimalPattern().IsMatch(raw)) throw new ArgumentException("Invalid decimal.");
        var parts = raw.TrimStart('-').Split('.');
        var fraction = parts.Length == 2 ? parts[1] : "";
        if (fraction.Length > scale) throw new ArgumentException("Decimal exceeds the field scale.");
        fraction = fraction.PadRight(scale, '0');
        var number = BigInteger.Parse(parts[0] + fraction, CultureInfo.InvariantCulture);
        if (raw.StartsWith('-')) number = -number;
        if (number < long.MinValue || number > long.MaxValue) throw new ArgumentException("Scaled decimal exceeds signed 64 bits.");
        var whole = parts[0].TrimStart('0');
        if (whole.Length == 0) whole = "0";
        var canonical = (number.Sign < 0 ? "-" : "") + whole + (scale > 0 ? "." + fraction : "");
        return (canonical, (long)number);
    }

    private static string NormalizeInstant(string value)
    {
        var match = InstantPattern().Match(value);
        if (!match.Success) throw new ArgumentException("Datetime must be RFC3339 with a time zone.");
        var fraction = match.Groups[2].Value.PadRight(9, '0');
        var parsed = DateTimeOffset.Parse(match.Groups[1].Value + "." + fraction[..7] + match.Groups[3].Value,
            CultureInfo.InvariantCulture, DateTimeStyles.None);
        return parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + fraction[7..] + "Z";
    }

    private async Task ValidateKeywordsAsync(string[] keywords, CancellationToken ct)
    {
        if (keywords.Length > 50 || keywords.Any(x => string.IsNullOrWhiteSpace(x) || x != x.Trim() || x.EnumerateRunes().Count() > 255)
            || keywords.Distinct(StringComparer.Ordinal).Count() != keywords.Length) throw new ArgumentException("Invalid keywords.", nameof(keywords));
        var count = await db.Set<TermsRow>().CountAsync(x => keywords.Contains(x.Id) && !x.Deprecated && x.MergedIntoId == null && x.AvailableAsKeyword, ct);
        if (count != keywords.Length) throw new ArgumentException("Keywords must be term IDs from the term store.", nameof(keywords));
    }

    private static bool Empty(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())
        || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;

    private static void ValidateRules(IReadOnlyList<SchemaField> fields, IReadOnlyList<SchemaRule> rules)
    {
        var byKey = fields.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (rules.Count > 32 || rules.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != rules.Count) throw new ArgumentException("Invalid schema rules.", nameof(rules));
        foreach (var rule in rules)
        {
            if (!FieldKey().IsMatch(rule.Key) || string.IsNullOrWhiteSpace(rule.Message) || !byKey.TryGetValue(rule.Field, out var left) || left.Options?.Multiple == true)
                throw new ArgumentException("Invalid schema rule.", nameof(rules));
            if (rule.Op == "required_if")
            {
                if (rule.WhenField is null || !byKey.TryGetValue(rule.WhenField, out var when) || when.Options?.Multiple == true || rule.WhenValue is null)
                    throw new ArgumentException("required_if needs a scalar condition.", nameof(rules));
            }
            else if (rule.Op is not ("eq" or "ne" or "gt" or "gte" or "lt" or "lte") || rule.OtherField is null
                || !byKey.TryGetValue(rule.OtherField, out var right) || left.Type != right.Type || left.Scale != right.Scale || right.Options?.Multiple == true)
                throw new ArgumentException("Comparison rules need matching scalar fields.", nameof(rules));
        }
    }

    private static void EvaluateRules(Dictionary<string, SchemaField> fields, IEnumerable<SchemaRule> rules, Dictionary<string, JsonElement> values)
    {
        foreach (var rule in rules)
        {
            values.TryGetValue(rule.Field, out var left);
            bool pass;
            if (rule.Op == "required_if")
            {
                values.TryGetValue(rule.WhenField!, out var when);
                pass = Empty(when) || Compare(fields[rule.WhenField!], when, rule.WhenValue!.Value) != 0 || !Empty(left);
            }
            else
            {
                values.TryGetValue(rule.OtherField!, out var right);
                if (Empty(left) || Empty(right)) continue;
                var compare = Compare(fields[rule.Field], left, right);
                pass = rule.Op switch { "eq" => compare == 0, "ne" => compare != 0, "gt" => compare > 0, "gte" => compare >= 0, "lt" => compare < 0, "lte" => compare <= 0, _ => false };
            }
            if (!pass) throw new ArgumentException(rule.Key + ": " + rule.Message);
        }
    }

    private static int Compare(SchemaField field, JsonElement left, JsonElement right) => field.Type switch
    {
        "integer" => left.GetInt64().CompareTo(right.GetInt64()),
        "decimal" => ScaledDecimal(left, field.Scale).Scaled.CompareTo(ScaledDecimal(right, field.Scale).Scaled),
        "number" or "currency" => left.GetDecimal().CompareTo(right.GetDecimal()),
        "boolean" => left.GetBoolean().CompareTo(right.GetBoolean()),
        "datetime" => string.CompareOrdinal(NormalizeInstant(left.GetString()!), NormalizeInstant(right.GetString()!)),
        _ => string.CompareOrdinal(left.GetString(), right.GetString()),
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex FieldKey();
    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?$")]
    private static partial Regex DecimalPattern();
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex InstantPattern();
}
