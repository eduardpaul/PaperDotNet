using Microsoft.Data.Sqlite;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>Snapshots with SQLite's online backup API: consistent while the database is in use (WAL).</summary>
internal sealed class SqliteDatabaseBackup(SqliteDatabaseSettings settings) : IDatabaseBackup
{
    public string FileName => "database.sqlite";

    public async Task BackupAsync(string file, CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection(settings.ConnectionString);
        await source.OpenAsync(cancellationToken);
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ConnectionString);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
    }

    public async Task RestoreAsync(string file, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString))
        await using (var target = new SqliteConnection(settings.ConnectionString))
        {
            await source.OpenAsync(cancellationToken);
            await target.OpenAsync(cancellationToken);
            source.BackupDatabase(target);
        }

        SqliteConnection.ClearAllPools();
    }
}
