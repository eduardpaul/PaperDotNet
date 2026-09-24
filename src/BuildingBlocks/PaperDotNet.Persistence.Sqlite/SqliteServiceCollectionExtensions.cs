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
        services.AddSingleton(new SqliteDatabaseSettings(ResolveConnectionString(configuration)));
        services.AddSingleton<IDatabaseProvider, SqliteDatabaseProvider>();
        services.AddSingleton<IFullTextSearch, SqliteFullTextSearch>();
        services.AddSingleton<IDatabaseBackup, SqliteDatabaseBackup>();
        services.AddHealthChecks().AddCheck<SqliteHealthCheck>("sqlite", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// The SQLite connection string: <c>ConnectionStrings:PaperDotNet</c>, or
    /// <c>{Storage:DataPath}/paperdotnet.db</c>. Creates the database directory.
    /// </summary>
    public static string ResolveConnectionString(IConfiguration configuration)
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

        return builder.ConnectionString;
    }

    /// <summary>Configures options for design-time tooling (dotnet ef).</summary>
    public static void ConfigureForDesignTime(DbContextOptionsBuilder options, string connectionString, string schema, string? migrationsAssembly = null) =>
        SqliteDatabaseProvider.Configure(options, connectionString, schema, migrationsAssembly);
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
