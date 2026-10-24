using Microsoft.Extensions.Configuration;

namespace PaperDotNet.Persistence.PostgreSql.Content;

public static class PostgreSqlContentBackup
{
    public static Task BackupAsync(string connectionString, IConfiguration configuration, string file, CancellationToken cancellationToken) =>
        new PostgreSqlDatabaseBackup(new PostgreSqlConnection(connectionString), configuration).BackupAsync(file, cancellationToken);

    public static Task RestoreAsync(string connectionString, IConfiguration configuration, string file, CancellationToken cancellationToken) =>
        new PostgreSqlDatabaseBackup(new PostgreSqlConnection(connectionString), configuration).RestoreAsync(file, cancellationToken);
}
