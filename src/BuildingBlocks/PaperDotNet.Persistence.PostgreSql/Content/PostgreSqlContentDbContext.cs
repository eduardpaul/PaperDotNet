using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Content;

namespace PaperDotNet.Persistence.PostgreSql.Content;

/// <summary>Provider for the pinned PaperGo schema. Uses its fingerprint instead of EF's history table.</summary>
public sealed partial class PostgreSqlContentDbContext(DbContextOptions<PostgreSqlContentDbContext> options) : ContentDbContext(options)
{
    public static PostgreSqlContentDbContext Open(string connectionString) => new(
        new DbContextOptionsBuilder<PostgreSqlContentDbContext>().UseNpgsql(connectionString).Options);

    public override DateTime NormalizeTimestamp(DateTime value) => new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    public override async Task LockWorkspaceAsync(string resourceId, CancellationToken cancellationToken) =>
        _ = await Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM resources WHERE id IN (SELECT workspace_id FROM resources WHERE id = {resourceId}) FOR UPDATE", cancellationToken);

    public override async Task LockLivePositionAsync(CancellationToken cancellationToken) =>
        _ = await Database.ExecuteSqlRawAsync("SELECT id FROM live_change_position WHERE id = 1 FOR UPDATE", cancellationToken);

    /// <summary>Initializes an empty database, or verifies an existing PaperGo fingerprint. Never upgrades unknown data.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = typeof(PostgreSqlContentDbContext).Assembly.GetManifestResourceStream(
            "PaperDotNet.Persistence.PostgreSql.Content.PaperGo.sql")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        await Database.OpenConnectionAsync(cancellationToken);

        await using var transaction = await Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        await Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7349539019032111)", cancellationToken);
        await using var command = Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count == 0)
        {
            // Reviewed provider migration: retain every constraint, partial index, seed and exact fingerprint.
            var migration = new PostgreSqlContentInitialMigration(sql);
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
