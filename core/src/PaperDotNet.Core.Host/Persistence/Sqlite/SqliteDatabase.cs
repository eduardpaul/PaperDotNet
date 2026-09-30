using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Core.Host.Data;

namespace PaperDotNet.Core.Host.Persistence.Sqlite;

/// <summary>
/// The SQLite database: connection string and schema. EF Core cannot run migrations under Native AOT (they need the
/// design-time model), so the migrations' SQL is generated at design time (eng/schema.sh) and embedded; this applies
/// the scripts not yet in <c>__EFMigrationsHistory</c>, each in a transaction (ADR-0039).
/// </summary>
internal static partial class SqliteDatabase
{
    private const string ResourcePrefix = "Schema.Sqlite.";

    public static string ConnectionString(IConfiguration configuration, string dataPath)
    {
        if (configuration.GetConnectionString("PaperDotNet") is { Length: > 0 } configured)
        {
            return configured;
        }

        Directory.CreateDirectory(dataPath);
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataPath, "paperdotnet.db"),
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Migration id and SQL of every embedded script, in order.</summary>
    public static IReadOnlyList<(string Id, string Sql)> Scripts()
    {
        var assembly = typeof(SqliteDatabase).Assembly;
        return [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => (n[ResourcePrefix.Length..^".sql".Length], Read(assembly, n)))];
    }

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDb>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SqliteDatabase));
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
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

            foreach (var (id, sql) in Scripts())
            {
                if (applied.Contains(id))
                {
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                LogApplied(logger, id);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
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
