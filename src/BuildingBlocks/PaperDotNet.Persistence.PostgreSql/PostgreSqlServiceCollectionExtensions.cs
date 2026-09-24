using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

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
        services.AddSingleton<IDatabaseProvider, PostgreSqlDatabaseProvider>();
        services.AddSingleton<IFullTextSearch, PostgreSqlFullTextSearch>();
        services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);
        return services;
    }

    /// <summary>Configures options for design-time tooling (dotnet ef).</summary>
    public static void ConfigureForDesignTime(DbContextOptionsBuilder options, string connectionString, string schema) =>
        PostgreSqlDatabaseProvider.Configure(options, NpgsqlDataSource.Create(connectionString), schema);
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
