using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>SQLite for every module DbContext (the default and, for now, only build, ADR-0039).</summary>
internal sealed class SqliteDatabaseProvider(SqliteDatabaseOptions options) : IDatabaseProvider
{
    public void Configure(DbContextOptionsBuilder builder) => builder.UseSqlite(options.ConnectionString);
}

public sealed record SqliteDatabaseOptions(string ConnectionString);

public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// Registers SQLite as the database: <c>ConnectionStrings:PaperDotNet</c>, or <c>paperdotnet.db</c> in
    /// <paramref name="dataPath"/>. Returns the connection string (Wolverine's message storage uses it too).
    /// </summary>
    public static string AddSqliteDatabase(this IServiceCollection services, IConfiguration configuration, string dataPath)
    {
        var connectionString = configuration.GetConnectionString("PaperDotNet") is { Length: > 0 } configured
            ? configured
            : new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Directory.CreateDirectory(dataPath).FullName, "paperdotnet.db"),
                DefaultTimeout = 30,
                Pooling = true,
            }.ToString();
        services.AddSingleton(new SqliteDatabaseOptions(connectionString));
        services.AddSingleton<IDatabaseProvider, SqliteDatabaseProvider>();
        services.AddSingleton<IAuditLogWriter, SqliteAuditLogWriter>();
        services.AddSingleton<IDatabaseBackup, SqliteDatabaseBackup>();
        return connectionString;
    }
}

/// <summary>
/// Options for the EF Core tools (<c>IDesignTimeDbContextFactory</c> of each module): the compiled model and the
/// precompiled queries are generated for SQLite, and migrations live in PaperDotNet.Migrations.Sqlite.
/// </summary>
public static class SqliteDesignTime
{
    public const string MigrationsAssembly = "PaperDotNet.Migrations.Sqlite";

    public static DbContextOptions<TContext> Options<TContext>()
        where TContext : DbContext => Options<TContext>(MigrationsAssembly);

    /// <summary>Design-time options with the migrations in <paramref name="migrationsAssembly"/> (an extension's companion project).</summary>
    public static DbContextOptions<TContext> Options<TContext>(string migrationsAssembly)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseSqlite("Data Source=design-time.db", sqlite => sqlite.MigrationsAssembly(migrationsAssembly))
            .Options;
}
