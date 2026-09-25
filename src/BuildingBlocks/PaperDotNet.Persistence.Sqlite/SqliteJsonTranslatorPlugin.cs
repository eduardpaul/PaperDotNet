using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>Translates <see cref="JsonFunctions"/> to SQLite JSON1 (<c>json_extract</c>, <c>json_type</c>) and <c>pdn_json_contains</c>.</summary>
internal sealed class SqliteJsonTranslatorPlugin(ISqlExpressionFactory factory) : IMethodCallTranslatorPlugin
{
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new Translator(factory)];

    private sealed class Translator(ISqlExpressionFactory sql) : IMethodCallTranslator
    {
        private static readonly bool[] PropagateBoth = [true, true];

        // json_extract and json_type return NULL for missing properties even when the document is not null.
        private static readonly bool[] PropagateNone = [false, false];

        public SqlExpression? Translate(
            SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments, IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (method.DeclaringType != typeof(JsonFunctions))
            {
                return null;
            }

            var document = arguments[0];
            return method.Name switch
            {
                nameof(JsonFunctions.Text) => Extract(document, arguments[1], typeof(string)),
                nameof(JsonFunctions.Number) => Extract(document, arguments[1], typeof(double)),
                nameof(JsonFunctions.Boolean) => Extract(document, arguments[1], typeof(bool)),
                nameof(JsonFunctions.HasProperty) => sql.IsNotNull(sql.Function("json_type", [document, Path(arguments[1])], nullable: true, PropagateNone, typeof(string))),
                nameof(JsonFunctions.Contains) => sql.Equal(
                    sql.Function(SqliteConnectionSetup.ContainsFunction, [document, arguments[1]], nullable: true, PropagateBoth, typeof(bool)),
                    sql.Constant(true)),
                _ => null,
            };
        }

        private SqlExpression Extract(SqlExpression document, SqlExpression property, Type type) =>
            sql.Function("json_extract", [document, Path(property)], nullable: true, PropagateNone, type);

        /// <summary>JSON path <c>$."name"</c>; property names are validated identifiers passed as constants.</summary>
        private SqlExpression Path(SqlExpression property) => property is SqlConstantExpression { Value: string name }
            ? sql.Constant($"$.\"{name}\"")
            : throw new InvalidOperationException("JSON property names must be constants.");
    }
}
