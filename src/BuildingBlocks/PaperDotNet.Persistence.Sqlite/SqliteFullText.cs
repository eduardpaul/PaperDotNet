using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>
/// Full-text search on SQLite with FTS5: an external-content table
/// <c>{table}_fts</c> kept in sync by triggers, ranked with <c>bm25</c>
/// (earlier columns weigh more: 10, 4, 1). Migrations create it with <see cref="CreateIndexSql"/>.
/// </summary>
public sealed class SqliteFullTextSearch : IFullTextSearch
{
    private static readonly double[] Weights = [10.0, 4.0, 1.0, 0.5];

    public IQueryable<FullTextMatch> Match<TEntity>(DbContext db, FullTextQuery query)
        where TEntity : class
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped.");
        var tableName = entityType.GetTableName()!;
        var table = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        var id = entityType.FindProperty("Id")!.GetColumnName(table)!;
        var columns = ((string?)entityType.FindAnnotation(FullTextExtensions.Annotation)?.Value
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no full-text index.")).Split(',');
        var fts = Quote(tableName + "_fts");
        var weights = string.Join(", ", columns.Select((_, i) => Weights[Math.Min(i, Weights.Length - 1)].ToString(CultureInfo.InvariantCulture)));
        var sql = $"SELECT d.{Quote(id)} AS \"{FullTextMatch.IdColumn}\", -bm25({fts}, {weights}) AS \"{FullTextMatch.RankColumn}\" " +
                  $"FROM {fts} JOIN {Quote(tableName)} AS d ON d.rowid = {fts}.rowid WHERE {fts} MATCH {{0}}";
        return db.Set<FullTextMatch>().FromSqlRaw(sql, Render(query));
    }

    /// <summary>Renders the query in FTS5 syntax: <c>AND</c>, <c>OR</c>, <c>NOT</c>, <c>"phrases"</c>, <c>"prefix"*</c>.</summary>
    internal static string Render(FullTextQuery query)
    {
        var positive = string.Join(" AND ", query.Groups.Select(g => g.Count == 1 ? Term(g[0]) : $"({string.Join(" OR ", g.Select(Term))})"));
        return query.Excluded.Count == 0
            ? positive
            : $"({positive}) NOT ({string.Join(" OR ", query.Excluded.Select(Term))})";
    }

    private static string Term(FullTextTerm term) => $"\"{string.Join(' ', term.Tokens)}\"{(term.Prefix ? "*" : string.Empty)}";

    /// <summary>
    /// DDL for a migration: the FTS5 table over <paramref name="columns"/> of
    /// <paramref name="table"/> (SQLite table name, e.g. <c>search_documents</c>) and the sync triggers.
    /// Re-create it after a migration that rebuilds the table.
    /// </summary>
    public static string CreateIndexSql(string table, params string[] columns)
    {
        var fts = Quote(table + "_fts");
        var list = string.Join(", ", columns.Select(Quote));
        var newValues = string.Join(", ", columns.Select(c => "new." + Quote(c)));
        var oldValues = string.Join(", ", columns.Select(c => "old." + Quote(c)));
        var t = Quote(table);
        return $"""
            CREATE VIRTUAL TABLE {fts} USING fts5({list}, content='{table}', content_rowid='rowid', tokenize='unicode61 remove_diacritics 0');
            CREATE TRIGGER {Quote(table + "_fts_ai")} AFTER INSERT ON {t} BEGIN
              INSERT INTO {fts}(rowid, {list}) VALUES (new.rowid, {newValues});
            END;
            CREATE TRIGGER {Quote(table + "_fts_ad")} AFTER DELETE ON {t} BEGIN
              INSERT INTO {fts}({fts}, rowid, {list}) VALUES ('delete', old.rowid, {oldValues});
            END;
            CREATE TRIGGER {Quote(table + "_fts_au")} AFTER UPDATE ON {t} BEGIN
              INSERT INTO {fts}({fts}, rowid, {list}) VALUES ('delete', old.rowid, {oldValues});
              INSERT INTO {fts}(rowid, {list}) VALUES (new.rowid, {newValues});
            END;
            INSERT INTO {fts}({fts}) VALUES ('rebuild');
            """;
    }

    public static string DropIndexSql(string table) =>
        $"""
        DROP TRIGGER IF EXISTS {Quote(table + "_fts_ai")};
        DROP TRIGGER IF EXISTS {Quote(table + "_fts_ad")};
        DROP TRIGGER IF EXISTS {Quote(table + "_fts_au")};
        DROP TABLE IF EXISTS {Quote(table + "_fts")};
        """;

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
