using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using PaperDotNet.Core.Host.Data;

namespace PaperDotNet.Core.Host.Persistence.Sqlite;

/// <summary>
/// Item queries on SQLite: the parsed OData tree becomes a parameterized SQL <c>WHERE</c>/<c>ORDER BY</c> over
/// <c>list_items</c> (field values through <c>json_extract</c>). Values are always parameters; column names come from a
/// fixed map and JSON paths from validated field names. Text comparisons follow SQLite (LIKE is ASCII case-insensitive).
/// Storage forms match EF Core's SQLite mappings: GUIDs as upper-case text, <see cref="DateTimeOffset"/> columns as
/// <c>yyyy-MM-dd HH:mm:ss.FFFFFFFzzz</c> in UTC, date-time field values in round-trip format (ADR-0039).
/// </summary>
internal sealed class SqliteItemQueries(CoreDb db) : IItemQueries
{
    private const string Columns = "\"i\".\"Id\", \"i\".\"ListId\", \"i\".\"Title\", \"i\".\"Fields\", \"i\".\"CreatedAt\", \"i\".\"CreatedBy\", \"i\".\"UpdatedAt\", \"i\".\"UpdatedBy\", \"i\".\"Version\"";

    public async Task<ItemQueryResult> QueryAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            var translator = new Translator(command);
            var where = new StringBuilder("\"i\".\"TenantId\" = ").Append(translator.Parameter(GuidText(query.TenantId)))
                .Append(" AND \"i\".\"ListId\" = ").Append(translator.Parameter(GuidText(query.ListId)));
            if (query.ItemId is { } itemId)
            {
                where.Append(" AND \"i\".\"Id\" = ").Append(translator.Parameter(GuidText(itemId)));
            }

            if (query.Filter is not null)
            {
                where.Append(" AND (").Append(translator.Predicate(query.Filter.Expression)).Append(')');
            }

            long? count = null;
            if (query.Count)
            {
                command.CommandText = $"SELECT COUNT(*) FROM \"list_items\" AS \"i\" WHERE {where}";
                count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            var sql = new StringBuilder($"SELECT {Columns} FROM \"list_items\" AS \"i\" WHERE ").Append(where);
            if (query.OrderBy is null)
            {
                // Keyset paging on the time-ordered id.
                if (query.Page.After is { } after)
                {
                    sql.Append(" AND \"i\".\"Id\" > ").Append(translator.Parameter(GuidText(after)));
                }

                sql.Append(" ORDER BY \"i\".\"Id\" LIMIT ").Append(translator.Parameter(query.Page.Top + 1));
            }
            else
            {
                sql.Append(" ORDER BY ");
                for (var clause = query.OrderBy; clause is not null; clause = clause.ThenBy)
                {
                    sql.Append(translator.Operand(clause.Expression).Sql).Append(clause.Direction == OrderByDirection.Descending ? " DESC, " : ", ");
                }

                sql.Append("\"i\".\"Id\" LIMIT ").Append(translator.Parameter(query.Page.Top + 1))
                    .Append(" OFFSET ").Append(translator.Parameter(query.Page.Offset));
            }

            command.CommandText = sql.ToString();
            var items = new List<ListItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(Read(reader));
            }

            return new ItemQueryResult(items, count);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static ListItem Read(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        ListId = Guid.Parse(reader.GetString(1)),
        Title = reader.GetString(2),
        Fields = reader.GetString(3),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        CreatedBy = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
        UpdatedBy = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
        Version = checked((uint)reader.GetInt64(8)),
    };

    /// <summary>EF Core's SQLite form of a GUID.</summary>
    internal static string GuidText(Guid value) => value.ToString("D").ToUpperInvariant();

    /// <summary>EF Core's SQLite form of a <see cref="DateTimeOffset"/> (stored in UTC by this application).</summary>
    internal static string TimestampText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy\\-MM\\-dd HH\\:mm\\:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    private enum OperandKind
    {
        Text,
        Number,
        Boolean,
        GuidColumn,
        TimestampColumn,
        FieldDateTime,
    }

    private readonly record struct Operand(string Sql, OperandKind Kind);

    private sealed class Translator(DbCommand command)
    {
        private static readonly Dictionary<string, Operand> ItemColumns = new(StringComparer.Ordinal)
        {
            ["id"] = new("\"i\".\"Id\"", OperandKind.GuidColumn),
            ["createdAt"] = new("\"i\".\"CreatedAt\"", OperandKind.TimestampColumn),
            ["updatedAt"] = new("\"i\".\"UpdatedAt\"", OperandKind.TimestampColumn),
            ["createdBy"] = new("\"i\".\"CreatedBy\"", OperandKind.GuidColumn),
            ["updatedBy"] = new("\"i\".\"UpdatedBy\"", OperandKind.GuidColumn),
        };

        public string Parameter(object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@p{command.Parameters.Count}";
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
            return parameter.ParameterName;
        }

        public string Predicate(QueryNode node) => Unwrap(node) switch
        {
            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And } and => $"({Predicate(and.Left)} AND {Predicate(and.Right)})",
            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Or } or => $"({Predicate(or.Left)} OR {Predicate(or.Right)})",
            BinaryOperatorNode comparison => Comparison(comparison),
            UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } not => $"(NOT {Predicate(not.Operand)})",
            SingleValueFunctionCallNode function => Function(function),
            InNode inNode => In(inNode),
            SingleValuePropertyAccessNode property when Operand(property) is { Kind: OperandKind.Boolean } boolean => $"({boolean.Sql} = 1)",
            ConstantNode { Value: bool value } => value ? "1 = 1" : "1 = 0",
            var other => throw Unsupported(other),
        };

        public Operand Operand(QueryNode node)
        {
            node = Unwrap(node);
            if (node is not SingleValuePropertyAccessNode property)
            {
                throw Unsupported(node);
            }

            var name = property.Property.Name;
            if (property.Source is SingleComplexNode)
            {
                if (name == FieldValues.Title)
                {
                    return new("\"i\".\"Title\"", OperandKind.Text);
                }

                // The name was checked against the list's fields by the parser and matches FieldValues' name pattern.
                var kind = property.Property.Type.PrimitiveKind() switch
                {
                    EdmPrimitiveTypeKind.Double => OperandKind.Number,
                    EdmPrimitiveTypeKind.Boolean => OperandKind.Boolean,
                    EdmPrimitiveTypeKind.DateTimeOffset => OperandKind.FieldDateTime,
                    _ => OperandKind.Text,
                };
                return new($"json_extract(\"i\".\"Fields\", {Parameter("$." + name)})", kind);
            }

            return ItemColumns.TryGetValue(name, out var column) ? column : throw Unsupported(node);
        }

        private string Comparison(BinaryOperatorNode node)
        {
            var op = node.OperatorKind switch
            {
                BinaryOperatorKind.Equal => "=",
                BinaryOperatorKind.NotEqual => "<>",
                BinaryOperatorKind.GreaterThan => ">",
                BinaryOperatorKind.GreaterThanOrEqual => ">=",
                BinaryOperatorKind.LessThan => "<",
                BinaryOperatorKind.LessThanOrEqual => "<=",
                _ => throw Unsupported(node),
            };

            var (left, right) = (Unwrap(node.Left), Unwrap(node.Right));
            if (left is ConstantNode && right is not ConstantNode)
            {
                (left, right) = (right, left);
                op = op switch { ">" => "<", ">=" => "<=", "<" => ">", "<=" => ">=", _ => op };
            }

            var operand = Operand(left);
            if (right is ConstantNode { Value: null })
            {
                return op switch
                {
                    "=" => $"{operand.Sql} IS NULL",
                    "<>" => $"{operand.Sql} IS NOT NULL",
                    _ => throw Unsupported(node),
                };
            }

            if (right is not ConstantNode constant)
            {
                throw Unsupported(node);
            }

            var sql = $"{operand.Sql} {op} {Parameter(Value(operand.Kind, constant.Value))}";

            // "ne" also matches items without a value, as in LINQ.
            return op == "<>" ? $"({sql} OR {operand.Sql} IS NULL)" : sql;
        }

        private string Function(SingleValueFunctionCallNode function)
        {
            var arguments = function.Parameters.ToList();
            if (arguments.Count != 2 || Unwrap(arguments[1]) is not ConstantNode { Value: string text })
            {
                throw Unsupported(function);
            }

            var operand = Operand(arguments[0]);
            var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            var pattern = function.Name switch
            {
                "contains" => $"%{escaped}%",
                "startswith" => $"{escaped}%",
                "endswith" => $"%{escaped}",
                _ => throw Unsupported(function),
            };
            return $"{operand.Sql} LIKE {Parameter(pattern)} ESCAPE '\\'";
        }

        private string In(InNode node)
        {
            var operand = Operand(node.Left);
            if (node.Right is not CollectionConstantNode values)
            {
                throw Unsupported(node);
            }

            if (values.Items.Count == 0)
            {
                return "1 = 0";
            }

            return $"{operand.Sql} IN ({string.Join(", ", values.Items.Select(v => Parameter(Value(operand.Kind, ((ConstantNode)v).Value))))})";
        }

        private static object? Value(OperandKind kind, object? value) => (kind, value) switch
        {
            (_, null) => null,
            (OperandKind.GuidColumn, Guid guid) => GuidText(guid),
            (OperandKind.TimestampColumn, DateTimeOffset date) => TimestampText(date),
            (OperandKind.FieldDateTime, DateTimeOffset date) => FieldValues.StorageDateTime(date),
            (OperandKind.Number, IConvertible number) => Convert.ToDouble(number, CultureInfo.InvariantCulture),
            (OperandKind.Boolean, bool boolean) => boolean ? 1 : 0,
            (OperandKind.Text, string text) => text,
            _ => throw new NotSupportedException($"The value '{value}' does not fit the property."),
        };

        private static QueryNode Unwrap(QueryNode node) => node is ConvertNode convert ? Unwrap(convert.Source) : node;

        private static NotSupportedException Unsupported(QueryNode node) =>
            new($"This query is not supported: {node.Kind}. Use comparisons, and/or/not, in, contains, startswith and endswith on fields.");
    }
}
