namespace PaperDotNet.Persistence.Sqlite.Content;

public static class SqliteContentBackup
{
    public static Task BackupAsync(string connectionString, string file, CancellationToken cancellationToken) =>
        new SqliteDatabaseBackup(new SqliteDatabaseSettings(connectionString)).BackupAsync(file, cancellationToken);

    public static Task RestoreAsync(string connectionString, string file, CancellationToken cancellationToken) =>
        new SqliteDatabaseBackup(new SqliteDatabaseSettings(connectionString)).RestoreAsync(file, cancellationToken);
}
