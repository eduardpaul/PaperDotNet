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

    public async Task WriteIndexColumnsAsync(Guid tenantId, Guid itemId, IReadOnlyDictionary<string, object?> columns, CancellationToken cancellationToken)
    {
        if (columns.Count == 0)
        {
            return;
        }

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            var translator = new Translator(command);
            var sets = columns.Select(c => FieldIndex.Columns.Contains(c.Key)
                ? $"\"{c.Key}\" = {translator.Parameter(c.Value)}"
                : throw new ArgumentException($"'{c.Key}' is not an indexed column.", nameof(columns)));
            command.CommandText = $"UPDATE \"list_items\" SET {string.Join(", ", sets)} "
                + $"WHERE \"TenantId\" = {translator.Parameter(GuidText(tenantId))} AND \"Id\" = {translator.Parameter(GuidText(itemId))}";
            await command.ExecuteNonQueryAsync(cancellationToken);
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

        translator.Indexed = query.Indexed;
        translator.TermFields = query.Parsed.TermFields;
        translator.TermDescendants = query.Parsed.TermDescendants;
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

    /// <summary>An SQL operand; <c>Field</c> names the list field it reads (for term subtrees).</summary>
    private readonly record struct Operand(string Sql, OperandKind Kind, string? Field = null);

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

        /// <summary>Indexed fields ready to use (ADR-0035): their item column or value-table rows instead of the JSON.</summary>
        public IReadOnlyDictionary<string, IndexedField>? Indexed { get; set; }

        /// <summary>Managed metadata fields and the subtrees of the terms in the filters (<see cref="TermHierarchy"/>).</summary>
        public IReadOnlySet<string>? TermFields { get; set; }

        public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? TermDescendants { get; set; }

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
                case SingleValuePropertyAccessNode { Source: SingleComplexNode } field when field.Property.Name == "title":
                    return new("\"i\".\"Title\"", OperandKind.Text);
                case SingleValuePropertyAccessNode { Source: SingleComplexNode } field:
                    // An indexed column holds the value as stored in the JSON (booleans as text: those stay on the JSON).
                    var kind = KindOf(field.Property.Type);
                    return Indexed?.GetValueOrDefault(field.Property.Name) is { Column: { } indexColumn } && FieldIndex.Columns.Contains(indexColumn)
                        && kind != OperandKind.Boolean
                        ? new($"\"i\".\"{indexColumn}\"", kind, field.Property.Name)
                        : new($"json_extract(\"i\".\"Fields\", {Parameter("$." + field.Property.Name)})", kind, field.Property.Name);
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

            if (op is "=" or "<>" && Subtree(operand, constant.Value) is { } subtree)
            {
                // A term and its descendants.
                var values = string.Join(", ", subtree.Select(id => Parameter(Value(operand.Kind, id))));
                return op == "=" ? $"{operand.Sql} IN ({values})" : $"({operand.Sql} NOT IN ({values}) OR {operand.Sql} IS NULL)";
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

            var items = values.Items.Select(Unwrap)
                .Select(v => v is ConstantNode constant ? constant.Value : throw Unsupported(v))
                .SelectMany(v => Subtree(operand, v) is { } subtree ? subtree.Cast<object?>() : [v])
                .Distinct()
                .ToList();
            return items.Count == 0
                ? "1 = 0"
                : $"{operand.Sql} IN ({string.Join(", ", items.Select(v => Parameter(Value(operand.Kind, v))))})";
        }

        /// <summary><c>fields/tags/any(t: …)</c>: some value of a multi-value field matches (no body: it has a value).</summary>
        private string Any(AnyNode node)
        {
            if (Unwrap(node.Source) is not CollectionPropertyAccessNode { Source: SingleComplexNode } collection)
            {
                throw Unsupported(node);
            }

            if (Indexed?.GetValueOrDefault(collection.Property.Name) is { Kind: IndexKinds.Values, ValueField: { } valueField } && ValueTableMatch(node, collection) is { } match)
            {
                return match(valueField);
            }

            var alias = $"\"j{_eachCount++}\"";
            var from = $"json_each(\"i\".\"Fields\", {Parameter("$." + collection.Property.Name)}) AS {alias}";
            if (node.Body is ConstantNode { Value: true } || node.CurrentRangeVariable is null)
            {
                return $"EXISTS (SELECT 1 FROM {from})";
            }

            _ranges[node.CurrentRangeVariable] = new($"{alias}.\"value\"", KindOf(collection.Property.Type), collection.Property.Name);
            return $"EXISTS (SELECT 1 FROM {from} WHERE {Predicate(node.Body)})";
        }

        /// <summary>
        /// <c>any()</c> and <c>any(x: x eq value)</c> on an indexed multi-value field, as a lookup in the value table;
        /// null for other bodies (they read the JSON).
        /// </summary>
        private Func<short, string>? ValueTableMatch(AnyNode node, CollectionPropertyAccessNode collection)
        {
            const string rows = "SELECT 1 FROM \"item_values\" AS \"v\" WHERE \"v\".\"ItemId\" = \"i\".\"Id\" AND \"v\".\"Field\" = ";
            if (node.Body is ConstantNode { Value: true } || node.CurrentRangeVariable is null)
            {
                return field => $"EXISTS ({rows}{Parameter(field)})";
            }

            if (Unwrap(node.Body) is not BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Equal } equal)
            {
                return null;
            }

            var (left, right) = (Unwrap(equal.Left), Unwrap(equal.Right));
            var constant = left as ConstantNode ?? right as ConstantNode;
            var reference = (left as NonResourceRangeVariableReferenceNode ?? right as NonResourceRangeVariableReferenceNode)?.RangeVariable;
            var text = constant?.Value switch
            {
                string value => value,
                Guid value => value.ToString(),
                _ => null,
            };
            if (text is null || reference != node.CurrentRangeVariable)
            {
                return null;
            }

            var element = new Operand(string.Empty, OperandKind.FieldIdentifier, collection.Property.Name);
            if (Guid.TryParse(text, out var term) && Subtree(element, term) is { } subtree)
            {
                var ids = subtree.Select(t => GuidText(FieldIndex.ValueId(collection.Property.Name, FieldFormats.Identifier(t)))).ToList();
                return field => $"EXISTS ({rows}{Parameter(field)} AND \"v\".\"Value\" IN ({string.Join(", ", ids.Select(i => Parameter(i)))}))";
            }

            var id = GuidText(FieldIndex.ValueId(collection.Property.Name, text));
            return field => $"EXISTS ({rows}{Parameter(field)} AND \"v\".\"Value\" = {Parameter(id)})";
        }

        /// <summary>The term and its descendants when <paramref name="operand"/> is a managed metadata field and the value one of the filters' terms.</summary>
        private IReadOnlyList<Guid>? Subtree(Operand operand, object? value) =>
            operand.Field is { } field && TermFields?.Contains(field) == true && value is Guid term && TermDescendants?.TryGetValue(term, out var subtree) == true
                ? subtree
                : null;

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
