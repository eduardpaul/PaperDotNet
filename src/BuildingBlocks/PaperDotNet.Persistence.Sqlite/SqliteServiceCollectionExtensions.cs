using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>Resolved SQLite connection string.</summary>
internal sealed record SqliteDatabaseSettings(string ConnectionString);

public static class SqliteServiceCollectionExtensions
{
    public const string ConnectionStringName = "PaperDotNet";

    /// <summary>
    /// Uses SQLite for every module DbContext (the default). Without a connection
    /// string the database is <c>{Storage:DataPath}/paperdotnet.db</c>.
    /// </summary>
    public static IServiceCollection AddPaperDotNetSqlite(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var dataPath = configuration["Storage:DataPath"] is { Length: > 0 } path ? path : "data";
            connectionString = $"Data Source={Path.Combine(dataPath, "paperdotnet.db")}";
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.DataSource is { Length: > 0 } file && file != ":memory:" && Path.GetDirectoryName(Path.GetFullPath(file)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        services.AddSingleton(new SqliteDatabaseSettings(builder.ConnectionString));
        services.AddSingleton<IDatabaseProvider, SqliteDatabaseProvider>();
        services.AddHealthChecks().AddCheck<SqliteHealthCheck>("sqlite", tags: ["ready"]);
        return services;
    }

    /// <summary>Configures options for design-time tooling (dotnet ef).</summary>
    public static void ConfigureForDesignTime(DbContextOptionsBuilder options, string connectionString, string schema) =>
        SqliteDatabaseProvider.Configure(options, connectionString, schema);
}

internal sealed class SqliteHealthCheck(SqliteDatabaseSettings settings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqliteConnection(settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (SqliteException ex)
        {
            return HealthCheckResult.Unhealthy("The SQLite database is not available.", ex);
        }
    }
}
