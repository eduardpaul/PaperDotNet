using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>
/// Keeps the query planner's statistics current with <c>PRAGMA optimize</c>, as SQLite recommends for long-lived
/// (pooled) connections. Without statistics SQLite takes every index as selective, so it drives joins through
/// <c>tenant_id</c> (every row of a single-tenant database) instead of the key, e.g. re-running a full-text match
/// for each search document. The pragma analyzes only tables that never were or have grown or shrunk tenfold, with
/// a time limit, so most runs do nothing.
/// </summary>
internal sealed partial class SqliteOptimizer(SqliteDatabaseSettings settings, TimeProvider time, ILogger<SqliteOptimizer> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first run waits one interval, so it starts after the startup migrations.
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await OptimizeAsync(settings.ConnectionString, stoppingToken);
            }
            catch (SqliteException ex)
            {
                LogOptimizeFailed(ex);
            }
        }
    }

    /// <summary>Checks the size of every table (0x10000) and analyzes those that need it (0x02).</summary>
    public static async Task OptimizeAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA optimize = 0x10002;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not update the SQLite query planner statistics.")]
    private partial void LogOptimizeFailed(Exception exception);
}
