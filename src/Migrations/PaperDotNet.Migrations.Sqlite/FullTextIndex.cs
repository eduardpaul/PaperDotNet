namespace PaperDotNet.Migrations.Sqlite;

/// <summary>
/// DDL of an FTS5 full-text index over columns of a table, for migrations: an external-content table
/// <c>{table}_fts</c> whose row ids are the table's integer key (declared, so they never change), kept in sync by
/// triggers. English words are stemmed (<c>porter</c>; SQLite has no per-row languages, SRC-05).
/// </summary>
internal static class FullTextIndex
{
    public static string Create(string table, string key, params string[] columns)
    {
        var fts = Quote(table + "_fts");
        var list = string.Join(", ", columns.Select(Quote));
        var newValues = string.Join(", ", columns.Select(c => "new." + Quote(c)));
        var oldValues = string.Join(", ", columns.Select(c => "old." + Quote(c)));
        var t = Quote(table);
        var k = Quote(key);
        return $"""
            CREATE VIRTUAL TABLE {fts} USING fts5({list}, content='{table}', content_rowid='{key}', tokenize='porter unicode61 remove_diacritics 2');
            CREATE TRIGGER {Quote(table + "_fts_ai")} AFTER INSERT ON {t} BEGIN
              INSERT INTO {fts}(rowid, {list}) VALUES (new.{k}, {newValues});
            END;
            CREATE TRIGGER {Quote(table + "_fts_ad")} AFTER DELETE ON {t} BEGIN
              INSERT INTO {fts}({fts}, rowid, {list}) VALUES ('delete', old.{k}, {oldValues});
            END;
            CREATE TRIGGER {Quote(table + "_fts_au")} AFTER UPDATE OF {list} ON {t} BEGIN
              INSERT INTO {fts}({fts}, rowid, {list}) VALUES ('delete', old.{k}, {oldValues});
              INSERT INTO {fts}(rowid, {list}) VALUES (new.{k}, {newValues});
            END;
            INSERT INTO {fts}({fts}) VALUES ('rebuild');
            """;
    }

    public static string Drop(string table) =>
        $"""
        DROP TRIGGER IF EXISTS {Quote(table + "_fts_ai")};
        DROP TRIGGER IF EXISTS {Quote(table + "_fts_ad")};
        DROP TRIGGER IF EXISTS {Quote(table + "_fts_au")};
        DROP TABLE IF EXISTS {Quote(table + "_fts")};
        """;

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
