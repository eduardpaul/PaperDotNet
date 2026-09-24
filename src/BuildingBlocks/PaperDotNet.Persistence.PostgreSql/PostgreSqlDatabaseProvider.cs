using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PaperDotNet.Persistence.PostgreSql;

internal sealed class PostgreSqlDatabaseProvider(NpgsqlDataSource dataSource) : IDatabaseProvider
{
    /// <summary>Assembly holding the PostgreSQL migrations of every module.</summary>
    public const string MigrationsAssembly = "PaperDotNet.Migrations.PostgreSql";

    public void Configure(DbContextOptionsBuilder options, string schema) =>
        Configure(options, dataSource, schema);

    public static void Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource, string schema)
    {
        options
            .UseNpgsql(dataSource, npgsql => npgsql
                .MigrationsAssembly(MigrationsAssembly)
                .MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention();
    }
}
