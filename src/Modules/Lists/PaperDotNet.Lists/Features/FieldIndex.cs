using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;

namespace PaperDotNet.Lists.Features;

/// <summary>How many item columns of each kind a list may use for indexed fields (ADR-0035).</summary>
public sealed class IndexedFieldLimits
{
    public int Text { get; set; } = FieldIndex.SlotsPerKind;

    public int Number { get; set; } = FieldIndex.SlotsPerKind;

    public int Date { get; set; } = FieldIndex.SlotsPerKind;

    public int For(string kind) => Math.Clamp(kind switch
    {
        IndexKinds.Text => Text,
        IndexKinds.Number => Number,
        _ => Date,
    }, 0, FieldIndex.SlotsPerKind);
}

/// <summary>
/// Indexed fields (ADR-0035): which column or value-table field a list uses for each indexed field, and what an item
/// writes there. <see cref="ListItem.Fields"/> stays the source; these are copies kept current on every save.
/// </summary>
internal static class FieldIndex
{
    /// <summary>Item columns per kind in the schema (<c>Text1..10</c>, <c>Number1..10</c>, <c>Date1..10</c>).</summary>
    public const int SlotsPerKind = 10;

    public const int MaxTextLength = 512;

    /// <summary>Values an item may have in one indexed multi-value field.</summary>
    public const int MaxValuesPerItem = 100;

    /// <summary>
    /// Fields of the built-in task and event content types take the same place in every list, so queries across lists
    /// (my tasks, the calendar) use one column.
    /// </summary>
    private static readonly Dictionary<string, string> PreferredColumns = new(StringComparer.Ordinal)
    {
        ["status"] = "Text1",
        ["priority"] = "Text2",
        ["dueDate"] = "Date1",
        ["start"] = "Date2",
        ["end"] = "Date3",
    };

    private static readonly Dictionary<string, short> PreferredValueFields = new(StringComparer.Ordinal)
    {
        ["assignedTo"] = 1,
        ["attendees"] = 2,
    };

    /// <summary>All item columns for indexed fields: the only column names the SQL of indexed fields uses.</summary>
    public static readonly IReadOnlySet<string> Columns = new[] { IndexKinds.Text, IndexKinds.Number, IndexKinds.Date }
        .SelectMany(kind => Enumerable.Range(1, SlotsPerKind).Select(n => ColumnName(kind, n)))
        .ToHashSet(StringComparer.Ordinal);

    public static string ColumnName(string kind, int number) => string.Create(CultureInfo.InvariantCulture, $"{kind}{number}");

    /// <summary>How a field is indexed, or null when it cannot be (long text, other multi-value kinds).</summary>
    public static string? KindOf(FieldDefinition field, IFieldType type)
    {
        if (field.AllowMultiple)
        {
            return type.ValueKind is FieldValueKind.Identifier or FieldValueKind.Text ? IndexKinds.Values : null;
        }

        return type.ValueKind switch
        {
            FieldValueKind.Identifier => IndexKinds.Values,
            FieldValueKind.Number => IndexKinds.Number,
            FieldValueKind.Date or FieldValueKind.DateTime => IndexKinds.Date,
            FieldValueKind.Boolean => IndexKinds.Text,
            FieldValueKind.Text when type.Name != "note" && (field.MaxLength ?? 255) <= MaxTextLength => IndexKinds.Text,
            _ => null,
        };
    }

    public static List<IndexedField> Read(ListDefinition list) =>
        JsonSerializer.Deserialize(list.IndexedFields, ListsJson.Default.ListIndexedField) ?? [];

    public static void Write(ListDefinition list, List<IndexedField> fields) =>
        list.IndexedFields = JsonSerializer.Serialize(fields, ListsJson.Default.ListIndexedField);

    /// <summary>The indexed fields queries may use (filled for every item), by field name.</summary>
    public static IReadOnlyDictionary<string, IndexedField> Ready(ListDefinition list) =>
        Read(list).Where(f => f.Ready).ToDictionary(f => f.Field, StringComparer.Ordinal);

    /// <summary>
    /// Brings the list's indexed fields in line with its content types: kept fields keep their place, new ones get a
    /// free column (or a value-table number); fields over the limit stay unindexed. New places are not ready until the
    /// backfill fills them, except in a new list. Returns true when something changed.
    /// </summary>
    public static bool Plan(ListDefinition list, IEnumerable<FieldDefinition> fields, FieldTypeRegistry types, IndexedFieldLimits limits, bool isNew)
    {
        var wanted = new List<(FieldDefinition Field, string Kind)>();
        foreach (var field in fields.Where(f => f.Indexed).DistinctBy(f => f.Name))
        {
            if (types.Find(field.Type) is { } type && KindOf(field, type) is { } kind)
            {
                wanted.Add((field, kind));
            }
        }

        var current = Read(list);
        var planned = current.Where(e => wanted.Any(w => w.Field.Name == e.Field && w.Kind == e.Kind)).ToList();
        var nextValueField = list.NextValueField;

        // Well-known fields first, so they get their preferred place.
        foreach (var (field, kind) in wanted.OrderBy(w => PreferredColumns.ContainsKey(w.Field.Name) || PreferredValueFields.ContainsKey(w.Field.Name) ? 0 : 1))
        {
            if (planned.Any(p => p.Field == field.Name))
            {
                continue;
            }

            if (kind == IndexKinds.Values)
            {
                var valueField = PreferredValueFields.TryGetValue(field.Name, out var preferred) && planned.All(p => p.ValueField != preferred)
                    ? preferred
                    : nextValueField++;
                planned.Add(new IndexedField { Field = field.Name, Kind = kind, ValueField = valueField, Ready = isNew });
                continue;
            }

            var used = planned.Where(p => p.Kind == kind).Select(p => p.Column).ToHashSet(StringComparer.Ordinal);
            var free = Enumerable.Range(1, limits.For(kind)).Select(n => ColumnName(kind, n)).Where(c => !used.Contains(c)).ToList();
            var column = PreferredColumns.TryGetValue(field.Name, out var preferredColumn) && free.Contains(preferredColumn) ? preferredColumn : free.FirstOrDefault();
            if (column is not null)
            {
                // Over the list's limit for this kind, the field stays unindexed.
                planned.Add(new IndexedField { Field = field.Name, Kind = kind, Column = column, Ready = isNew });
            }
        }

        static string Describe(IEnumerable<IndexedField> entries) =>
            string.Join(";", entries.OrderBy(e => e.Field, StringComparer.Ordinal).Select(e => $"{e.Field}:{e.Kind}:{e.Column}:{e.ValueField}:{e.Ready}"));
        if (Describe(planned) == Describe(current) && nextValueField == list.NextValueField)
        {
            return false;
        }

        Write(list, planned);
        list.NextValueField = nextValueField;
        list.IndexPending = planned.Any(p => !p.Ready);
        return true;
    }

    /// <summary>The column values and value-table values of stored field values (every planned column gets a value, or null).</summary>
    public static (Dictionary<string, object?> Columns, Dictionary<short, HashSet<Guid>> Values) Compute(string fields, IReadOnlyList<IndexedField> indexed)
    {
        var columns = new Dictionary<string, object?>(StringComparer.Ordinal);
        var result = new Dictionary<short, HashSet<Guid>>();
        if (indexed.Count == 0)
        {
            return (columns, result);
        }

        var values = JsonNode.Parse(fields) as JsonObject ?? [];
        foreach (var entry in indexed)
        {
            var value = values[entry.Field];
            if (entry.Kind == IndexKinds.Values)
            {
                result[entry.ValueField!.Value] = [.. Ids(entry.Field, value).Take(MaxValuesPerItem)];
            }
            else if (entry.Column is { } column && Columns.Contains(column))
            {
                columns[column] = ColumnValue(entry.Kind, value);
            }
        }

        return (columns, result);
    }

    /// <summary>The value an item column holds for a stored JSON value.</summary>
    public static object? ColumnValue(string kind, JsonNode? value)
    {
        if (value is not JsonValue single)
        {
            return null;
        }

        return kind switch
        {
            IndexKinds.Number when single.TryGetValue<double>(out var number) => number,
            IndexKinds.Number when single.TryGetValue<decimal>(out var number) => (double)number,
            IndexKinds.Number => null,
            IndexKinds.Text when single.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
            _ when single.TryGetValue<string>(out var text) => text.Length > MaxTextLength ? text[..MaxTextLength] : text,
            _ => single.ToJsonString(),
        };
    }

    /// <summary>The ids a stored value holds in the value table: GUIDs as they are, text (choices) as name-based ids.</summary>
    public static IEnumerable<Guid> Ids(string field, JsonNode? value)
    {
        IEnumerable<JsonNode?> items = value is JsonArray array ? array : new[] { value };
        foreach (var item in items)
        {
            if (item is JsonValue single && single.TryGetValue<string>(out var text) && text.Length > 0)
            {
                yield return ValueId(field, text);
            }
        }
    }

    /// <summary>The value-table id of a value: a GUID itself, or a name-based id of the field and the text.</summary>
    public static Guid ValueId(string field, string text)
    {
        if (Guid.TryParse(text, out var id))
        {
            return id;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{field}\n{text}"), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // RFC 9562 version 8 (custom)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
