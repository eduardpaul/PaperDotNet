using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;

namespace PaperDotNet.Persistence.PostgreSql;

/// <summary>
/// Translates <see cref="JsonFunctions"/> to PostgreSQL: <c>jsonb_extract_path_text</c>
/// (with casts), <c>jsonb_exists</c> and the index-friendly containment operator <c>@&gt;</c>.
/// </summary>
internal sealed class PostgreSqlJsonTranslatorPlugin(ISqlExpressionFactory factory, IRelationalTypeMappingSource typeMappings)
    : IMethodCallTranslatorPlugin
{
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new Translator((NpgsqlSqlExpressionFactory)factory, typeMappings)];

    private sealed class Translator(NpgsqlSqlExpressionFactory sql, IRelationalTypeMappingSource typeMappings) : IMethodCallTranslator
    {
        private static readonly bool[] PropagateFirst = [true, false];

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
                nameof(JsonFunctions.Text) => ExtractText(document, arguments[1]),
                nameof(JsonFunctions.Number) => sql.Convert(ExtractText(document, arguments[1]), typeof(double), typeMappings.FindMapping(typeof(double))),
                nameof(JsonFunctions.Boolean) => sql.Convert(ExtractText(document, arguments[1]), typeof(bool), typeMappings.FindMapping(typeof(bool))),
                nameof(JsonFunctions.HasProperty) => sql.Function("jsonb_exists", [document, arguments[1]], nullable: true, PropagateFirst, typeof(bool)),
                nameof(JsonFunctions.Contains) => sql.Contains(document, sql.ApplyTypeMapping(arguments[1], typeMappings.FindMapping("jsonb"))),
                _ => null,
            };
        }

        private SqlExpression ExtractText(SqlExpression document, SqlExpression property) =>
            // NULL for missing properties even when the document is not null: nullability must not follow the arguments.
            sql.Function("jsonb_extract_path_text", [document, property], nullable: true, [false, false], typeof(string));
    }
}
