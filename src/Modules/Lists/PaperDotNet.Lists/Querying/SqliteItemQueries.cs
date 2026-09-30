using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;

namespace PaperDotNet.Lists.Querying;

/// <summary>
/// Item queries on SQLite: the parsed OData tree becomes a parameterized SQL <c>WHERE</c>/<c>ORDER BY</c> over
/// <c>list_items</c> (field values through <c>json_extract</c>, multiple values through <c>json_each</c>). Values are
/// always parameters; column names come from a fixed map and JSON paths from validated field names. Text comparisons
/// follow SQLite (LIKE is ASCII case-insensitive). Storage forms match EF Core's SQLite mappings: GUID columns as
/// upper-case text, <see cref="DateTimeOffset"/> columns as <c>yyyy-MM-dd HH:mm:ss.FFFFFFFzzz</c> in UTC; field values
/// as <see cref="FieldFormats"/> writes them (ADR-0039).
/// </summary>
internal sealed class SqliteItemQueries(ListsDbContext db, TimeProvider time) : IItemQueries
{
    private const string Columns =
        "\"i\".\"Id\", \"i\".\"ListId\", \"i\".\"ContentTypeId\", \"i\".\"ParentId\", \"i\".\"IsFolder\", \"i\".\"ScopeId\", \"i\".\"Title\", \"i\".\"Fields\", "
        + "\"i\".\"CreatedAt\", \"i\".\"CreatedBy\", \"i\".\"UpdatedAt\", \"i\".\"UpdatedBy\", \"i\".\"Version\", \"i\".\"HasUniquePermissions\"";

    public async Task<int> MoveScopeAsync(Guid tenantId, Guid parentId, Guid oldScope, Guid newScope, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            // The change log for delta gets the items (not those in the recycle bin) with the scope they came from.
            const string moving = "WHERE \"TenantId\" = $tenant AND \"ParentId\" = $parent AND \"HasUniquePermissions\" = 0 AND \"ScopeId\" = $old";
            command.CommandText =
                "INSERT INTO \"item_changes\" (\"TenantId\", \"ListId\", \"ItemId\", \"ScopeId\", \"FromScopeId\", \"Kind\", \"At\") "
                + $"SELECT \"TenantId\", \"ListId\", \"Id\", $new, $old, '{ItemChangeKinds.Upserted}', $at FROM \"list_items\" {moving} AND \"DeletedAt\" IS NULL; "
                + $"UPDATE \"list_items\" SET \"ScopeId\" = $new {moving}; SELECT changes();";
            foreach (var (name, value) in new[] { ("$new", newScope), ("$tenant", tenantId), ("$parent", parentId), ("$old", oldScope) })
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = GuidText(value);
                command.Parameters.Add(parameter);
            }

            var at = command.CreateParameter();
            at.ParameterName = "$at";
            at.Value = time.GetUtcNow().ToUnixTimeMilliseconds();
            command.Parameters.Add(at);

            // One transaction: the log and the move are saved together.
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            command.Transaction = transaction;
            var moved = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            await transaction.CommitAsync(cancellationToken);
            return moved;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<ItemQueryResult> QueryAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        if (query.ListIds.Count == 0 || query.Scopes is { Count: 0 })
        {
            return new ItemQueryResult([], query.Count ? 0 : null, false);
        }

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            var translator = new Translator(command);
            var where = Where(query, translator);

            long? count = null;
            if (query.Count)
            {
                command.CommandText = $"SELECT COUNT(*) FROM \"list_items\" AS \"i\" WHERE {where}";
                count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            var sql = new StringBuilder($"SELECT {Columns} FROM \"list_items\" AS \"i\" WHERE ").Append(where);
            if (query.Parsed.OrderBy is null)
            {
                // Keyset paging on the time-ordered id.
                if (query.Cursor.After is { } after)
                {
                    sql.Append(" AND \"i\".\"Id\" > ").Append(translator.Parameter(GuidText(after)));
                }

                sql.Append(" ORDER BY \"i\".\"Id\" LIMIT ").Append(translator.Parameter(query.Top + 1));
            }
            else
            {
                translator.Aliases = query.Parsed.OrderAliases;
                sql.Append(" ORDER BY ");
                for (var clause = query.Parsed.OrderBy; clause is not null; clause = clause.ThenBy)
                {
                    sql.Append(translator.Operand(clause.Expression).Sql).Append(clause.Direction == OrderByDirection.Descending ? " DESC, " : ", ");
                }

                sql.Append("\"i\".\"Id\" LIMIT ").Append(translator.Parameter(query.Top + 1))
                    .Append(" OFFSET ").Append(translator.Parameter(query.Cursor.Offset));
            }

            command.CommandText = sql.ToString();
            var items = new List<ListItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(Read(reader, query.TenantId));
            }

            var hasMore = items.Count > query.Top;
            if (hasMore)
            {
                items.RemoveAt(items.Count - 1);
            }

            return new ItemQueryResult(items, count, hasMore);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>The WHERE clause of an item query: tenant, lists, readable scopes, folders and filters.</summary>
    private static string Where(ItemQuery query, Translator translator)
    {
        var where = new StringBuilder("\"i\".\"TenantId\" = ").Append(translator.Parameter(GuidText(query.TenantId)))
                .Append(" AND \"i\".\"DeletedAt\" IS NULL AND \"i\".\"ListId\" IN (")
                .Append(string.Join(", ", query.ListIds.Select(id => translator.Parameter(GuidText(id))))).Append(')');
        if (query.Scopes is { } scopes)
        {
            where.Append(" AND \"i\".\"ScopeId\" IN (").Append(string.Join(", ", scopes.Select(id => translator.Parameter(GuidText(id))))).Append(')');
        }

        where.Append(query.Folders switch
        {
            FolderMode.ItemsOnly => " AND \"i\".\"IsFolder\" = 0",
            FolderMode.Children when query.ParentId is { } parent => $" AND \"i\".\"ParentId\" = {translator.Parameter(GuidText(parent))}",
            FolderMode.Children => " AND \"i\".\"ParentId\" IS NULL",
            _ => "",
        });
        if (query.ItemId is { } itemId)
        {
            where.Append(" AND \"i\".\"Id\" = ").Append(translator.Parameter(GuidText(itemId)));
        }

        foreach (var (clause, aliases) in query.Parsed.Filters)
        {
            translator.Aliases = aliases;
            where.Append(" AND (").Append(translator.Predicate(clause.Expression)).Append(')');
        }

        return where.ToString();
    }

    public async Task<IReadOnlyList<ValueCount>> CountValuesAsync(ItemQuery query, string field, bool multiple, CancellationToken cancellationToken)
    {
        if (query.ListIds.Count == 0 || query.Scopes is { Count: 0 })
        {
            return [];
        }

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            var translator = new Translator(command);
            var where = Where(query, translator);
            var path = translator.Parameter("$." + field);
            var limit = translator.Parameter(query.Top);
            var counts = new List<ValueCount>();

            // The type is grouped too, so true and 1 stay apart; values come back as text.
            command.CommandText = multiple
                ? $"SELECT \"j\".\"type\", \"j\".\"value\", COUNT(*) FROM \"list_items\" AS \"i\", json_each(\"i\".\"Fields\", {path}) AS \"j\" "
                  + $"WHERE {where} GROUP BY 1, 2 ORDER BY 3 DESC LIMIT {limit}"
                : $"SELECT json_type(\"i\".\"Fields\", {path}), json_extract(\"i\".\"Fields\", {path}), COUNT(*) FROM \"list_items\" AS \"i\" "
                  + $"WHERE {where} GROUP BY 1, 2 ORDER BY 3 DESC LIMIT {limit}";
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    counts.Add(new ValueCount(reader.IsDBNull(0) ? null : ValueText(reader.GetString(0), reader.GetValue(1)), reader.GetInt64(2)));
                }
            }

            if (multiple)
            {
                // Items without any value.
                command.CommandText = $"SELECT COUNT(*) FROM \"list_items\" AS \"i\" WHERE {where} "
                    + $"AND COALESCE(json_array_length(\"i\".\"Fields\", {path}), 0) = 0";
                var empty = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (empty > 0)
                {
                    counts.Add(new ValueCount(null, empty));
                }
            }

            return counts;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>A JSON value as the API shows it in counts: numbers in invariant form, booleans as true/false.</summary>
    private static string? ValueText(string type, object value) => type switch
    {
        "null" => null,
        "true" => "true",
        "false" => "false",
        "integer" => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        "real" => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static ListItem Read(DbDataReader reader, Guid tenantId) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        TenantId = tenantId,
        ListId = Guid.Parse(reader.GetString(1)),
        ContentTypeId = Guid.Parse(reader.GetString(2)),
        ParentId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
        IsFolder = reader.GetInt64(4) != 0,
        ScopeId = Guid.Parse(reader.GetString(5)),
        Title = reader.GetString(6),
        Fields = reader.GetString(7),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        CreatedBy = reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
        UpdatedBy = reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
        Version = checked((uint)reader.GetInt64(12)),
        HasUniquePermissions = reader.GetInt64(13) != 0,
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
        FieldDate,
        FieldDateTime,
        FieldIdentifier,
    }

    private readonly record struct Operand(string Sql, OperandKind Kind);

    private sealed class Translator(DbCommand command)
    {
        private static readonly Dictionary<string, Operand> ItemColumns = new(StringComparer.Ordinal)
        {
            ["id"] = new("\"i\".\"Id\"", OperandKind.GuidColumn),
            ["contentTypeId"] = new("\"i\".\"ContentTypeId\"", OperandKind.GuidColumn),
            ["parentId"] = new("\"i\".\"ParentId\"", OperandKind.GuidColumn),
            ["isFolder"] = new("\"i\".\"IsFolder\"", OperandKind.Boolean),
            ["createdAt"] = new("\"i\".\"CreatedAt\"", OperandKind.TimestampColumn),
            ["updatedAt"] = new("\"i\".\"UpdatedAt\"", OperandKind.TimestampColumn),
            ["createdBy"] = new("\"i\".\"CreatedBy\"", OperandKind.GuidColumn),
            ["updatedBy"] = new("\"i\".\"UpdatedBy\"", OperandKind.GuidColumn),
        };

        private readonly Dictionary<RangeVariable, Operand> _ranges = [];
        private int _eachCount;

        /// <summary>The parameter aliases of the clause being translated (<c>@me</c>, <c>@today</c>, …).</summary>
        public IDictionary<string, QueryNode>? Aliases { get; set; }

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
            AnyNode any => Any(any),
            ConstantNode { Value: bool value } => value ? "1 = 1" : "1 = 0",
            var other when Operand(other) is { Kind: OperandKind.Boolean } boolean => $"({boolean.Sql} = 1)",
            var other => throw Unsupported(other),
        };

        public Operand Operand(QueryNode node)
        {
            node = Unwrap(node);
            switch (node)
            {
                case SingleValueFunctionCallNode { Name: "tolower" or "toupper" } call when call.Parameters.Count() == 1:
                    var inner = Operand(call.Parameters.First());
                    return new($"{(call.Name == "tolower" ? "lower" : "upper")}({inner.Sql})", inner.Kind);
                case NonResourceRangeVariableReferenceNode reference when _ranges.TryGetValue(reference.RangeVariable, out var element):
                    return element;
                case SingleValuePropertyAccessNode { Source: SingleComplexNode } field:
                    return field.Property.Name == "title"
                        ? new("\"i\".\"Title\"", OperandKind.Text)
                        : new($"json_extract(\"i\".\"Fields\", {Parameter("$." + field.Property.Name)})", KindOf(field.Property.Type));
                case SingleValuePropertyAccessNode property when ItemColumns.TryGetValue(property.Property.Name, out var column):
                    return column;
                default:
                    throw Unsupported(node);
            }
        }

        /// <summary>How a field's values are stored in the item JSON, from its EDM type (see <c>ItemEdmModel</c>).</summary>
        private static OperandKind KindOf(IEdmTypeReference type) =>
            (type.IsCollection() ? type.AsCollection().ElementType() : type).PrimitiveKind() switch
            {
                EdmPrimitiveTypeKind.Decimal => OperandKind.Number,
                EdmPrimitiveTypeKind.Boolean => OperandKind.Boolean,
                EdmPrimitiveTypeKind.Date => OperandKind.FieldDate,
                EdmPrimitiveTypeKind.DateTimeOffset => OperandKind.FieldDateTime,
                EdmPrimitiveTypeKind.Guid => OperandKind.FieldIdentifier,
                _ => OperandKind.Text,
            };

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

            var items = values.Items.Select(Unwrap).ToList();
            return items.Count == 0
                ? "1 = 0"
                : $"{operand.Sql} IN ({string.Join(", ", items.Select(v => Parameter(Value(operand.Kind, v is ConstantNode constant ? constant.Value : throw Unsupported(v)))))})";
        }

        /// <summary><c>fields/tags/any(t: …)</c>: some value of a multi-value field matches (no body: it has a value).</summary>
        private string Any(AnyNode node)
        {
            if (Unwrap(node.Source) is not CollectionPropertyAccessNode { Source: SingleComplexNode } collection)
            {
                throw Unsupported(node);
            }

            var alias = $"\"j{_eachCount++}\"";
            var from = $"json_each(\"i\".\"Fields\", {Parameter("$." + collection.Property.Name)}) AS {alias}";
            if (node.Body is ConstantNode { Value: true } || node.CurrentRangeVariable is null)
            {
                return $"EXISTS (SELECT 1 FROM {from})";
            }

            _ranges[node.CurrentRangeVariable] = new($"{alias}.\"value\"", KindOf(collection.Property.Type));
            return $"EXISTS (SELECT 1 FROM {from} WHERE {Predicate(node.Body)})";
        }

        private static object? Value(OperandKind kind, object? value) => (kind, value) switch
        {
            (_, null) => null,
            (OperandKind.GuidColumn, Guid guid) => GuidText(guid),
            (OperandKind.FieldIdentifier, Guid guid) => FieldFormats.Identifier(guid),
            (OperandKind.TimestampColumn, DateTimeOffset date) => TimestampText(date),
            (OperandKind.FieldDateTime, DateTimeOffset date) => FieldFormats.DateTime(date),
            (OperandKind.FieldDate, DateOnly date) => FieldFormats.Date(date),
            (OperandKind.FieldDate, DateTimeOffset date) => FieldFormats.Date(DateOnly.FromDateTime(date.UtcDateTime)),
            (OperandKind.Number, IConvertible number) => Convert.ToDouble(number, CultureInfo.InvariantCulture),
            (OperandKind.Boolean, bool boolean) => boolean ? 1 : 0,
            (OperandKind.Text, string text) => text,
            _ => throw new NotSupportedException($"The value '{value}' does not fit the property."),
        };

        private QueryNode Unwrap(QueryNode node) => node switch
        {
            ConvertNode convert => Unwrap(convert.Source),
            ParameterAliasNode alias when Aliases is not null && Aliases.TryGetValue(alias.Alias, out var value) && value is not null => Unwrap(value),
            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract } arithmetic
                when Unwrap(arithmetic.Left) is ConstantNode left && Unwrap(arithmetic.Right) is ConstantNode right =>
                new ConstantNode(Arithmetic(arithmetic, left.Value, right.Value)),
            _ => node,
        };

        /// <summary>Date arithmetic on constants, e.g. <c>@today add duration'P30D'</c>, computed before the query runs.</summary>
        private static object Arithmetic(BinaryOperatorNode node, object? left, object? right)
        {
            var sign = node.OperatorKind == BinaryOperatorKind.Add ? 1 : -1;
            return (left, right) switch
            {
                (DateOnly date, TimeSpan duration) => date.AddDays(sign * (int)duration.TotalDays),
                (DateTimeOffset date, TimeSpan duration) => date + (sign * duration),
                _ => throw Unsupported(node),
            };
        }

        private static NotSupportedException Unsupported(QueryNode node) =>
            new($"This query is not supported: {node.Kind}. Use comparisons, and/or/not, in, any, contains, startswith, endswith, tolower and toupper on fields.");
    }
}
