using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>
/// The SQLite schema. EF Core cannot run migrations under Native AOT (they need the design-time model), so the SQL of
/// every module's migrations is generated at design time (<c>eng/schema.sh</c>) and embedded here; this applies the
/// scripts not yet in <c>__EFMigrationsHistory</c>, in order of their migration ids, each in a transaction (ADR-0039).
/// </summary>
public static partial class SqliteDatabase
{
    private const string ResourcePrefix = "Schema.";

    /// <summary>The prefix of the SQLite scripts of tables outside the modules (<see cref="SchemaScripts"/>).</summary>
    public const string ExtensionResourcePrefix = "Schema.Sqlite.";

    /// <summary>Migration id and SQL of every embedded script of the modules, in order.</summary>
    public static IReadOnlyList<(string Id, string Sql)> Scripts() => Scripts(typeof(SqliteDatabase).Assembly, ResourcePrefix);

    /// <summary>Migration id and SQL of the scripts in <paramref name="assembly"/> whose resource names start with <paramref name="prefix"/>, in order.</summary>
    public static IReadOnlyList<(string Id, string Sql)> Scripts(Assembly assembly, string prefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => (n[prefix.Length..^".sql".Length], Read(assembly, n)))];
    }

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<SqliteDatabaseOptions>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SqliteDatabase));
        await using var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            // Readers do not block the writer; set once, kept in the database file.
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
            pragma.CommandText = "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using (var history = connection.CreateCommand())
        {
            history.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\";";
            await using var reader = await history.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetString(0));
            }
        }

        // The modules first, then the tables of extensions (which only reference their own tables).
        var scripts = Scripts().Concat(services.GetServices<SchemaScripts>()
            .DistinctBy(s => s.Assembly)
            .OrderBy(s => s.Owner, StringComparer.Ordinal)
            .SelectMany(s => Scripts(s.Assembly, ExtensionResourcePrefix)));
        foreach (var (id, sql) in scripts)
        {
            if (applied.Contains(id))
            {
                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogApplied(logger, id);
        }
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied the database migration {MigrationId}.")]
    private static partial void LogApplied(ILogger logger, string migrationId);
}
