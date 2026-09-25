using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence.PostgreSql;

public static class PostgreSqlServiceCollectionExtensions
{
    public const string ConnectionStringName = "PaperDotNet";

    /// <summary>Uses PostgreSQL for every module DbContext (<c>Database:Provider = PostgreSql</c>).</summary>
    public static IServiceCollection AddPaperDotNetPostgreSql(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is missing. Set PAPERDOTNET__ConnectionStrings__{ConnectionStringName}.");
        }

        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);

            // Kerberos/GSS is rarely used for self-hosting; skip probing for its native library unless configured.
            if (!connectionString.Contains("GSS", StringComparison.OrdinalIgnoreCase))
            {
                builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
            }

            return builder.Build();
        });
        // Row-level security as a second tenant wall (Database:RowLevelSecurity, default on).
        services.AddSingleton(new PostgreSqlSettings(configuration.GetValue("Database:RowLevelSecurity", defaultValue: true)));
        services.AddSingleton<IDatabaseProvider, PostgreSqlDatabaseProvider>();
        services.AddSingleton<IFullTextSearch, PostgreSqlFullTextSearch>();
        services.AddSingleton(new PostgreSqlConnection(connectionString));
        services.AddSingleton<IDatabaseBackup, PostgreSqlDatabaseBackup>();
        services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);

        // Live events reach clients on every server (ADR-0026).
        services.AddSingleton<PostgreSqlLiveEventBackplane>();
        services.AddSingleton<ILiveEventBackplane>(sp => sp.GetRequiredService<PostgreSqlLiveEventBackplane>());
        services.AddHostedService(sp => sp.GetRequiredService<PostgreSqlLiveEventBackplane>());
        return services;
    }

    /// <summary>Configures options for design-time tooling (dotnet ef).</summary>
    public static void ConfigureForDesignTime(DbContextOptionsBuilder options, string connectionString, string schema, string? migrationsAssembly = null) =>
        PostgreSqlDatabaseProvider.Configure(options, NpgsqlDataSource.Create(connectionString), schema, migrationsAssembly);
}

internal sealed class PostgreSqlHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.", ex);
        }
    }
}
