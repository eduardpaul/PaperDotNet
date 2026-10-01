using Microsoft.Data.Sqlite;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>Snapshots with SQLite's online backup API: consistent while the database is in use.</summary>
internal sealed class SqliteDatabaseBackup(SqliteDatabaseOptions options) : IDatabaseBackup
{
    public string Provider => "sqlite";

    public string FileName => "database.sqlite";

    public async Task BackupAsync(string file, CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection(options.ConnectionString);
        await source.OpenAsync(cancellationToken);
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
    }

    public async Task RestoreAsync(string file, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        await using (var target = new SqliteConnection(options.ConnectionString))
        {
            await source.OpenAsync(cancellationToken);
            await target.OpenAsync(cancellationToken);
            source.BackupDatabase(target);
        }

        SqliteConnection.ClearAllPools();
    }
}
