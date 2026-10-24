using Npgsql;
using PaperDotNet.Content;
using PaperDotNet.Persistence.PostgreSql.Content;
using PaperDotNet.Persistence.Sqlite.Content;
using Testcontainers.PostgreSql;
using Xunit;

namespace PaperDotNet.Content.Tests;

public sealed class ContentFixture : IAsyncLifetime
{
    private PostgreSqlContainer? postgres;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "paperdotnet-content-" + Guid.NewGuid().ToString("N"));
    public static bool IsPostgres => string.Equals(Environment.GetEnvironmentVariable("PAPERDOTNET_TEST_PROVIDER"), "postgresql", StringComparison.OrdinalIgnoreCase);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        if (IsPostgres)
        {
            postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await postgres.StartAsync();
        }
    }

    public async Task<ContentDbContext> OpenAsync()
    {
        if (postgres is null)
        {
            var db = SqliteContentDbContext.Open("Data Source=" + Path.Combine(directory, Guid.NewGuid() + ".db"));
            await db.InitializeAsync();
            return db;
        }
        var name = "content_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE DATABASE " + name;
        await command.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = name };
        var pg = PostgreSqlContentDbContext.Open(builder.ConnectionString);
        await pg.InitializeAsync();
        return pg;
    }

    public async ValueTask DisposeAsync()
    {
        if (postgres is not null) await postgres.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
