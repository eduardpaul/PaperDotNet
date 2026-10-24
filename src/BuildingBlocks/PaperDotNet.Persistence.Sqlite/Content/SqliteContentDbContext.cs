using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Content;

namespace PaperDotNet.Persistence.Sqlite.Content;

/// <summary>Provider for the pinned PaperGo schema. Uses its fingerprint instead of EF's history table.</summary>
public sealed partial class SqliteContentDbContext(DbContextOptions<SqliteContentDbContext> options) : ContentDbContext(options)
{
    public static SqliteContentDbContext Open(string connectionString) => new(
        new DbContextOptionsBuilder<SqliteContentDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder(connectionString) { ForeignKeys = true, DefaultTimeout = 30 }.ConnectionString).Options);

    public override Task LockWorkspaceAsync(string resourceId, CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task LockLivePositionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Initializes an empty database, or verifies an existing PaperGo fingerprint. Never upgrades unknown data.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = typeof(SqliteContentDbContext).Assembly.GetManifestResourceStream(
            "PaperDotNet.Persistence.Sqlite.Content.PaperGo.sql")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        await Database.OpenConnectionAsync(cancellationToken);
        await Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA wal_autocheckpoint = 256; PRAGMA journal_size_limit = 1048576;", cancellationToken);
        await using var transaction = await Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);

        await using var command = Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count == 0)
        {
            // Reviewed provider migration: retain every constraint, partial index, seed and exact fingerprint.
            var migration = new SqliteContentInitialMigration(sql);
            var commands = this.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, Model);
            await this.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(commands,
                this.GetService<IRelationalConnection>(), new MigrationExecutionState(), commitTransaction: false,
                cancellationToken: cancellationToken);
            Set<SchemaMetadataRow>().Add(new SchemaMetadataRow { Id = 1, Hash = hash });
            await SaveChangesAsync(cancellationToken);
        }
        else
        {
            if (count != 64) throw new InvalidOperationException("Expected an empty database or the pinned 64-table PaperGo schema. The modular PaperDotNet database cannot be reused here.");
            command.CommandText = "SELECT hash FROM schema_metadata WHERE id = 1";
            var actual = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Database is not the pinned PaperGo schema. Use a new database or an explicitly reviewed migration.");
            }
        }
        await transaction.CommitAsync(cancellationToken);
        ChangeTracker.Clear();
    }
}
