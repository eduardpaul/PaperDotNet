using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;

namespace PaperDotNet.Lists.Features;

/// <summary>How many item columns of each kind a list may use for indexed fields (ADR-0035).</summary>
public sealed class IndexedFieldLimits
{
    public int Text { get; set; } = FieldIndex.SlotsPerKind;

    public int Number { get; set; } = FieldIndex.SlotsPerKind;

    public int Date { get; set; } = FieldIndex.SlotsPerKind;

    public int For(IndexKind kind) => Math.Clamp(kind switch
    {
        IndexKind.Text => Text,
        IndexKind.Number => Number,
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

    /// <summary>Value-table field numbers below this are for the well-known fields.</summary>
    public const short FirstCustomValueField = 16;

    /// <summary>
    /// Fields of the built-in task and event content types take the same place in every list, so queries across
    /// lists (my tasks, the calendar) use one column.
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

    private static readonly Dictionary<string, PropertyInfo> Columns = typeof(ListItem).GetProperties()
        .Where(p => p.Name.Length > 4 && (p.Name.StartsWith("Text", StringComparison.Ordinal) || p.Name.StartsWith("Number", StringComparison.Ordinal)
                                           || p.Name.StartsWith("Date", StringComparison.Ordinal))
                    && char.IsDigit(p.Name[^1]))
        .ToDictionary(p => p.Name, StringComparer.Ordinal);

    /// <summary>How a field is indexed, or null when it cannot be (long text, other multi-value kinds).</summary>
    public static IndexKind? KindOf(FieldDefinition field, IFieldType type)
    {
        if (field.AllowMultiple)
        {
            return type.ValueKind is FieldValueKind.Identifier or FieldValueKind.Text ? IndexKind.Values : null;
        }

        return type.ValueKind switch
        {
            FieldValueKind.Identifier => IndexKind.Values,
            FieldValueKind.Number => IndexKind.Number,
            FieldValueKind.Date or FieldValueKind.DateTime => IndexKind.Date,
            FieldValueKind.Boolean => IndexKind.Text,
            FieldValueKind.Text when type.Name != "note" && (field.MaxLength ?? 255) <= MaxTextLength => IndexKind.Text,
            _ => null,
        };
    }

    public static string ColumnName(IndexKind kind, int number) => $"{kind}{number}";

    /// <summary>
    /// Brings <see cref="ListDefinition.IndexedFields"/> in line with the list's indexed fields: kept fields keep their
    /// place, new ones get a free column (or a value-table number); fields over the limit stay unindexed. New places
    /// are not ready until the backfill fills them, except in a new list. Returns true when something changed.
    /// </summary>
    public static bool Plan(
        ListDefinition list, IEnumerable<FieldDefinition> fields, Func<string, IFieldType?> types, IndexedFieldLimits limits, bool isNew)
    {
        var wanted = new List<(FieldDefinition Field, IndexKind Kind)>();
        foreach (var field in fields.Where(f => f.Indexed).DistinctBy(f => f.Name))
        {
            if (types(field.Type) is { } type && KindOf(field, type) is { } kind)
            {
                wanted.Add((field, kind));
            }
        }

        var planned = list.IndexedFields
            .Where(e => wanted.Any(w => w.Field.Name == e.Field && w.Kind == e.Kind))
            .Select(e => new IndexedField { Field = e.Field, Kind = e.Kind, Column = e.Column, ValueField = e.ValueField, Ready = e.Ready })
            .ToList();
        var nextValueField = list.NextValueField;

        // Well-known fields first, so they get their preferred place.
        foreach (var (field, kind) in wanted.OrderBy(w => PreferredColumns.ContainsKey(w.Field.Name) || PreferredValueFields.ContainsKey(w.Field.Name) ? 0 : 1))
        {
            if (planned.Any(p => p.Field == field.Name))
            {
                continue;
            }

            var entry = new IndexedField { Field = field.Name, Kind = kind, Ready = isNew };
            if (kind == IndexKind.Values)
            {
                entry.ValueField = PreferredValueFields.TryGetValue(field.Name, out var preferred) && planned.All(p => p.ValueField != preferred)
                    ? preferred
                    : nextValueField++;
            }
            else
            {
                var used = planned.Where(p => p.Kind == kind).Select(p => p.Column).ToHashSet(StringComparer.Ordinal);
                var free = Enumerable.Range(1, limits.For(kind)).Select(n => ColumnName(kind, n)).Where(c => !used.Contains(c)).ToList();
                entry.Column = PreferredColumns.TryGetValue(field.Name, out var column) && free.Contains(column) ? column : free.FirstOrDefault();
                if (entry.Column is null)
                {
                    continue; // Over the list's limit for this kind: the field stays unindexed.
                }
            }

            planned.Add(entry);
        }

        static string Describe(IEnumerable<IndexedField> entries) =>
            string.Join(";", entries.OrderBy(e => e.Field, StringComparer.Ordinal).Select(e => $"{e.Field}:{e.Kind}:{e.Column}:{e.ValueField}:{e.Ready}"));
        if (Describe(planned) == Describe(list.IndexedFields) && nextValueField == list.NextValueField)
        {
            return false;
        }

        list.IndexedFields = planned;
        list.NextValueField = nextValueField;
        list.IndexPending = planned.Any(p => !p.Ready);
        return true;
    }

    /// <summary>
    /// Writes the item's indexed single values into its columns and returns the values of its value-table fields
    /// (at most <see cref="MaxValuesPerItem"/> each).
    /// </summary>
    public static Dictionary<short, HashSet<Guid>> Apply(ListItem item, IReadOnlyList<IndexedField> indexed)
    {
        var (columns, values) = Compute(item.Fields, indexed);
        foreach (var (column, value) in columns)
        {
            Columns[column].SetValue(item, value);
        }

        return values;
    }

    /// <summary>The column values and value-table values of stored field values.</summary>
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
            if (entry.Kind == IndexKind.Values)
            {
                result[entry.ValueField!.Value] = [.. Ids(entry.Field, value).Take(MaxValuesPerItem)];
            }
            else if (entry.Column is { } column && Columns.ContainsKey(column))
            {
                columns[column] = ColumnValue(entry.Kind, value);
            }
        }

        return (columns, result);
    }

    /// <summary>The value an item column holds for a stored JSON value.</summary>
    public static object? ColumnValue(IndexKind kind, JsonNode? value)
    {
        if (value is not JsonValue single)
        {
            return null;
        }

        return kind switch
        {
            IndexKind.Number when single.TryGetValue<double>(out var number) => number,
            IndexKind.Number when single.TryGetValue<decimal>(out var number) => (double)number,
            IndexKind.Number => null,
            IndexKind.Text when single.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
            _ when single.TryGetValue<string>(out var text) => text.Length > MaxTextLength ? text[..MaxTextLength] : text,
            _ => Convert.ToString(single.GetValue<object>(), CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The ids a stored value holds in the value table: GUIDs as they are, text (choices) as name-based ids.</summary>
    public static IEnumerable<Guid> Ids(string field, JsonNode? value)
    {
        IEnumerable<JsonNode?> items = value is JsonArray array ? array : [value];
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

    /// <summary>The indexed field a query may use (ready), or null to read the JSON.</summary>
    public static IndexedField? Ready(ListDefinition list, string field) =>
        list.IndexedFields.FirstOrDefault(e => e.Field == field && e.Ready);
}
