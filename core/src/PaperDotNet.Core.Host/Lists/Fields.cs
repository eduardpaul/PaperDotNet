using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PaperDotNet.Core.Host.Lists;

/// <summary>A field of a list. <see cref="Name"/> is the key in item <c>fields</c> and in <c>$filter</c> (<c>fields/name</c>).</summary>
public sealed record FieldDefinition(string Name, string Type, bool Required = false, IReadOnlyList<string>? Choices = null, string? DisplayName = null);

public static class FieldTypes
{
    public const string Text = "text";
    public const string Note = "note";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string DateTime = "dateTime";
    public const string Choice = "choice";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Text, Note, Number, Boolean, DateTime, Choice };
}

/// <summary>Validation of field definitions and of item values against them (storage form, ADR-0039).</summary>
public static partial class FieldValues
{
    public const string Title = "title";
    public const int MaxTextLength = 255;
    public const int MaxNoteLength = 65536;

    private static readonly HashSet<string> Reserved = new([Title, "id", "listId", "createdAt", "createdBy", "updatedAt", "updatedBy"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex NamePattern();

    /// <summary>Errors in a set of field definitions (names, types, choices), keyed like request fields.</summary>
    public static Dictionary<string, string[]> Validate(IReadOnlyList<FieldDefinition> fields)
    {
        var errors = new Dictionary<string, string[]>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < fields.Count; n++)
        {
            var field = fields[n];
            var key = $"fields[{n}]";
            if (field.Name is null || !NamePattern().IsMatch(field.Name) || Reserved.Contains(field.Name))
            {
                errors[key] = ["A field name starts with a letter, has letters, digits or _ (up to 64) and is not reserved."];
            }
            else if (!names.Add(field.Name))
            {
                errors[key] = [$"The field '{field.Name}' is defined twice."];
            }
            else if (field.Type is null || !FieldTypes.All.Contains(field.Type))
            {
                errors[key] = [$"Unknown field type '{field.Type}'. Use one of: {string.Join(", ", FieldTypes.All)}."];
            }
            else if (field.Type == FieldTypes.Choice && (field.Choices is not { Count: > 0 } || field.Choices.Any(string.IsNullOrEmpty)))
            {
                errors[key] = ["A choice field needs at least one non-empty choice."];
            }
        }

        return errors;
    }

    /// <summary>
    /// Merges <paramref name="input"/> into <paramref name="stored"/> (a copy of the item's values, or empty for a new
    /// item): known fields are checked and normalized, <c>null</c> removes a value, <c>title</c> is returned apart.
    /// </summary>
    public static Dictionary<string, string[]> Merge(IReadOnlyList<FieldDefinition> fields, JsonObject input, JsonObject stored, ref string title)
    {
        var errors = new Dictionary<string, string[]>();
        var byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        foreach (var (name, value) in input)
        {
            var key = $"fields.{name}";
            if (name == Title)
            {
                if (value is JsonValue text && text.GetValueKind() == JsonValueKind.String && text.GetValue<string>().Trim() is { Length: > 0 and <= MaxTextLength } trimmed)
                {
                    title = trimmed;
                }
                else
                {
                    errors[key] = [$"The title is a text of 1 to {MaxTextLength} characters."];
                }

                continue;
            }

            if (!byName.TryGetValue(name, out var field))
            {
                errors[key] = [$"The list has no field '{name}'."];
                continue;
            }

            if (value is null)
            {
                stored.Remove(name);
                continue;
            }

            if (Normalize(field, value) is { } normalized)
            {
                stored[name] = normalized;
            }
            else
            {
                errors[key] = [Expected(field)];
            }
        }

        if (title.Length == 0 && !errors.ContainsKey($"fields.{Title}"))
        {
            errors[$"fields.{Title}"] = ["The title is required."];
        }

        foreach (var field in fields)
        {
            if (field.Required && !stored.ContainsKey(field.Name) && !errors.ContainsKey($"fields.{field.Name}"))
            {
                errors[$"fields.{field.Name}"] = [$"The field '{field.Name}' is required."];
            }
        }

        return errors;
    }

    /// <summary>The item's values as the API shows them: the title and the values of the list's current fields.</summary>
    public static JsonObject ForApi(IReadOnlyList<FieldDefinition> fields, string title, string storedJson)
    {
        var stored = JsonNode.Parse(storedJson)?.AsObject() ?? [];
        var result = new JsonObject { [Title] = title };
        foreach (var field in fields)
        {
            if (stored[field.Name] is { } value)
            {
                result[field.Name] = value.DeepClone();
            }
        }

        return result;
    }

    /// <summary>A date-time value in storage form: UTC, round-trip format, so text order is time order.</summary>
    public static string StorageDateTime(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static JsonValue? Normalize(FieldDefinition field, JsonNode value)
    {
        if (value is not JsonValue scalar)
        {
            return null;
        }

        var kind = scalar.GetValueKind();
        return field.Type switch
        {
            FieldTypes.Text when kind == JsonValueKind.String && scalar.GetValue<string>() is { Length: <= MaxTextLength } text => JsonValue.Create(text),
            FieldTypes.Note when kind == JsonValueKind.String && scalar.GetValue<string>() is { Length: <= MaxNoteLength } note => JsonValue.Create(note),
            FieldTypes.Number when kind == JsonValueKind.Number && scalar.GetValue<double>() is var number && double.IsFinite(number) => JsonValue.Create(number),
            FieldTypes.Boolean when kind is JsonValueKind.True or JsonValueKind.False => JsonValue.Create(kind == JsonValueKind.True),
            FieldTypes.DateTime when kind == JsonValueKind.String
                && DateTimeOffset.TryParse(scalar.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) => JsonValue.Create(StorageDateTime(date)),
            FieldTypes.Choice when kind == JsonValueKind.String && field.Choices is { } choices && choices.Contains(scalar.GetValue<string>(), StringComparer.Ordinal) => JsonValue.Create(scalar.GetValue<string>()),
            _ => null,
        };
    }

    private static string Expected(FieldDefinition field) => field.Type switch
    {
        FieldTypes.Text => $"Expected a text of up to {MaxTextLength} characters.",
        FieldTypes.Note => $"Expected a text of up to {MaxNoteLength} characters.",
        FieldTypes.Number => "Expected a number.",
        FieldTypes.Boolean => "Expected true or false.",
        FieldTypes.DateTime => "Expected an ISO 8601 date and time.",
        FieldTypes.Choice => $"Expected one of: {string.Join(", ", field.Choices ?? [])}.",
        _ => "Unsupported value.",
    };
}
