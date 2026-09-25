using Microsoft.EntityFrameworkCore;
using WorkflowCore.Persistence.Sqlite;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>
/// WorkflowCore's SQLite storage only creates its tables in a new database (<c>EnsureCreated</c>) and ships
/// no migrations; our database already exists, so its tables are created here from its model when missing.
/// </summary>
public static class SqliteWorkflowTables
{
    public static async Task EnsureAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var context = new SqliteContext(connectionString);
        var exists = await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'Workflow'")
            .SingleAsync(cancellationToken);
        if (exists == 0)
        {
            await context.Database.ExecuteSqlRawAsync(context.Database.GenerateCreateScript(), cancellationToken);
        }
    }
}
