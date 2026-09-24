using System.Collections.Frozen;
using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Lists.Fields;

internal class TextFieldType : FieldType
{
    public override string Name => "text";

    public override FieldValueKind ValueKind => FieldValueKind.Text;


    protected virtual int DefaultMaxLength => 255;

    protected virtual int MaxAllowedLength => 4000;

    public override IEnumerable<string> ValidateDefinition(FieldDefinition field)
    {
        foreach (var error in base.ValidateDefinition(field))
        {
            yield return error;
        }

        if (field.MaxLength is < 1 || field.MaxLength > MaxAllowedLength)
        {
            yield return $"maxLength must be between 1 and {MaxAllowedLength}.";
        }
    }

    protected override ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return Done(FieldValueResult.Fail("A text value is expected."));
        }

        var text = value.GetString()!;
        var max = field.MaxLength ?? DefaultMaxLength;
        return Done(text.Length > max
            ? FieldValueResult.Fail($"At most {max} characters are allowed.")
            : Validate(text) ?? FieldValueResult.Ok(JsonValue.Create(text)));
    }

    /// <summary>Extra validation for derived text types; null when valid.</summary>
    protected virtual FieldValueResult? Validate(string text) => null;
}

internal sealed class NoteFieldType : TextFieldType
{
    public override string Name => "note";

    protected override int DefaultMaxLength => 100_000;

    protected override int MaxAllowedLength => 1_000_000;
}

internal sealed class EmailFieldType : TextFieldType
{
    public override string Name => "email";

    protected override FieldValueResult? Validate(string text) =>
        MailAddress.TryCreate(text, out var address) && address.Address == text
            ? null
            : FieldValueResult.Fail("A valid e-mail address is expected.");
}

internal sealed class UrlFieldType : TextFieldType
{
    public override string Name => "url";

    protected override int DefaultMaxLength => 2048;

    protected override FieldValueResult? Validate(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? null
            : FieldValueResult.Fail("An absolute http(s) URL is expected.");
}

internal class NumberFieldType : FieldType
{
    public override string Name => "number";

    public override FieldValueKind ValueKind => FieldValueKind.Number;


    public override IEnumerable<string> ValidateDefinition(FieldDefinition field)
    {
        foreach (var error in base.ValidateDefinition(field))
        {
            yield return error;
        }

        if (field.Minimum > field.Maximum)
        {
            yield return "minimum must not be greater than maximum.";
        }
    }

    protected override ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
        {
            return Done(FieldValueResult.Fail("A number is expected."));
        }

        if (number < field.Minimum || number > field.Maximum)
        {
            return Done(FieldValueResult.Fail($"The value must be between {field.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "-∞"} and {field.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "∞"}."));
        }

        return Done(FieldValueResult.Ok(JsonValue.Create(number)));
    }
}

internal sealed partial class CurrencyFieldType : NumberFieldType
{
    public override string Name => "currency";

    public override IEnumerable<string> ValidateDefinition(FieldDefinition field)
    {
        foreach (var error in base.ValidateDefinition(field))
        {
            yield return error;
        }

        if (field.CurrencyCode is null || !CurrencyCode().IsMatch(field.CurrencyCode))
        {
            yield return "currencyCode must be an ISO 4217 code such as EUR.";
        }
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}

internal sealed class BooleanFieldType : FieldType
{
    public override string Name => "boolean";

    public override FieldValueKind ValueKind => FieldValueKind.Boolean;


    protected override ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken) =>
        Done(value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? FieldValueResult.Ok(JsonValue.Create(value.GetBoolean()))
            : FieldValueResult.Fail("true or false is expected."));
}

internal sealed class DateFieldType : FieldType
{
    public override string Name => "date";

    public override FieldValueKind ValueKind => FieldValueKind.Date;


    protected override ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken) =>
        Done(value.ValueKind == JsonValueKind.String
             && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? FieldValueResult.Ok(JsonValue.Create(FieldFormats.Date(date)))
            : FieldValueResult.Fail("A date (yyyy-MM-dd) is expected."));
}

internal sealed class DateTimeFieldType : FieldType
{
    public override string Name => "dateTime";

    public override FieldValueKind ValueKind => FieldValueKind.DateTime;


    protected override ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken) =>
        Done(value.ValueKind == JsonValueKind.String
             && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
             && value.GetString()!.Contains('T', StringComparison.Ordinal)
            ? FieldValueResult.Ok(JsonValue.Create(FieldFormats.DateTime(moment)))
            : FieldValueResult.Fail("An ISO 8601 date and time with offset is expected."));
}

internal sealed class ChoiceFieldType : FieldType
{
    public override string Name => "choice";

    public override FieldValueKind ValueKind => FieldValueKind.Text;

    public override bool SupportsMultiple => true;


    public override IEnumerable<string> ValidateDefinition(FieldDefinition field)
    {
        foreach (var error in base.ValidateDefinition(field))
        {
            yield return error;
        }

        if (field.Choices.Count == 0 || field.Choices.Any(string.IsNullOrWhiteSpace))
        {
            yield return "choices must contain at least one non-empty value.";
        }

        if (field.Choices.Distinct(StringComparer.Ordinal).Count() != field.Choices.Count)
        {
            yield return "choices must be unique.";
        }
    }

    protected override ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken) =>
        Done(value.ValueKind == JsonValueKind.String && field.Choices.Contains(value.GetString()!)
            ? FieldValueResult.Ok(JsonValue.Create(value.GetString()))
            : FieldValueResult.Fail($"One of the choices is expected: {string.Join(", ", field.Choices)}."));
}

internal sealed class PersonFieldType : FieldType
{
    public override string Name => "person";

    public override FieldValueKind ValueKind => FieldValueKind.Identifier;

    public override bool SupportsMultiple => true;


    protected override async ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken) =>
        value.ValueKind == JsonValueKind.String && System.Guid.TryParse(value.GetString(), out var userId)
            && await context.UserExistsAsync(userId, cancellationToken)
            ? FieldValueResult.Ok(JsonValue.Create(FieldFormats.Identifier(userId)))
            : FieldValueResult.Fail("The id of an active user of the organization is expected.");
}

internal sealed class LookupFieldType : FieldType
{
    public override string Name => "lookup";

    public override FieldValueKind ValueKind => FieldValueKind.Identifier;

    public override bool SupportsMultiple => true;


    public override IEnumerable<string> ValidateDefinition(FieldDefinition field)
    {
        foreach (var error in base.ValidateDefinition(field))
        {
            yield return error;
        }

        if (field.LookupListId is null)
        {
            yield return "lookupListId is required.";
        }
    }

    protected override async ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken) =>
        value.ValueKind == JsonValueKind.String && System.Guid.TryParse(value.GetString(), out var itemId)
            && await context.ItemExistsAsync(field.LookupListId!.Value, itemId, cancellationToken)
            ? FieldValueResult.Ok(JsonValue.Create(FieldFormats.Identifier(itemId)))
            : FieldValueResult.Fail("The id of an item in the lookup list is expected.");
}

/// <summary>
/// Managed metadata: terms of one term set (SharePoint "Managed Metadata"). Values
/// are term ids; a label is accepted and resolved (and added when the set is open).
/// Filters on a term also match its descendants.
/// </summary>
internal sealed class ManagedMetadataFieldType : FieldType
{
    public const string TypeName = "managedMetadata";

    public override string Name => TypeName;

    public override FieldValueKind ValueKind => FieldValueKind.Identifier;

    public override bool SupportsMultiple => true;


    public override IEnumerable<string> ValidateDefinition(FieldDefinition field)
    {
        foreach (var error in base.ValidateDefinition(field))
        {
            yield return error;
        }

        if (field.TermSetId is null)
        {
            yield return "termSetId is required.";
        }
    }

    protected override async ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            && await context.ResolveTermAsync(field.TermSetId!.Value, value.GetString()!, cancellationToken) is { } termId
            ? FieldValueResult.Ok(JsonValue.Create(FieldFormats.Identifier(termId)))
            : FieldValueResult.Fail("The id or label of an active term of the term set is expected.");
}

/// <summary>
/// Enterprise keywords (folksonomy): free tags from the tenant's keywords set.
/// Values are term ids; unknown labels become new keywords.
/// </summary>
internal sealed class KeywordsFieldType : FieldType
{
    public const string TypeName = "keywords";

    public override string Name => TypeName;

    public override FieldValueKind ValueKind => FieldValueKind.Identifier;

    public override bool SupportsMultiple => true;


    public override IEnumerable<string> ValidateDefinition(FieldDefinition field)
    {
        foreach (var error in base.ValidateDefinition(field))
        {
            yield return error;
        }

        if (field.TermSetId is not null)
        {
            yield return "termSetId is not used: keywords always come from the keywords term set.";
        }
    }

    protected override async ValueTask<FieldValueResult> NormalizeSingleAsync(JsonElement value, FieldDefinition field, IFieldValidationContext context, CancellationToken cancellationToken) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            && await context.ResolveTermAsync(null, value.GetString()!, cancellationToken) is { } termId
            ? FieldValueResult.Ok(JsonValue.Create(FieldFormats.Identifier(termId)))
            : FieldValueResult.Fail("A keyword (text) or the id of an active keyword is expected.");
}

/// <summary>All registered field types, by name.</summary>
public sealed partial class FieldTypeRegistry(IEnumerable<IFieldType> types)
{
    /// <summary>Names reserved for built-in item properties.</summary>
    public static readonly FrozenSet<string> ReservedNames = new[]
    {
        "title", "id", "fields", "contentType", "contentTypeId", "parentId", "isFolder",
        "createdAt", "createdBy", "updatedAt", "updatedBy", "listId",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenDictionary<string, IFieldType> _types = types.ToFrozenDictionary(t => t.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<IFieldType> All => _types.Values;

    public IFieldType? Find(string name) => _types.GetValueOrDefault(name);

    /// <summary>Validates a field definition; returns all problems found.</summary>
    public IEnumerable<string> Validate(FieldDefinition field)
    {
        if (!FieldName().IsMatch(field.Name))
        {
            yield return $"Field name '{field.Name}' must start with a lowercase letter and contain only letters and digits (max 63).";
        }
        else if (ReservedNames.Contains(field.Name))
        {
            yield return $"Field name '{field.Name}' is reserved.";
        }

        if (string.IsNullOrWhiteSpace(field.DisplayName))
        {
            yield return $"Field '{field.Name}' needs a displayName.";
        }

        if (Find(field.Type) is not { } type)
        {
            yield return $"Unknown field type '{field.Type}'. Known types: {string.Join(", ", _types.Keys.Order(StringComparer.Ordinal))}.";
            yield break;
        }

        foreach (var error in type.ValidateDefinition(field))
        {
            yield return $"Field '{field.Name}': {error}";
        }
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,62}$")]
    private static partial Regex FieldName();
}
