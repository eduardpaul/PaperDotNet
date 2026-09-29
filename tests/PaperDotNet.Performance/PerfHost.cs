using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

// The host assembly is named paperdotnet, which is not a project folder. The test host would
// otherwise look for <repo>/paperdotnet. This path is relative to the build output.
[assembly: WebApplicationFactoryContentRoot(
    "paperdotnet, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null",
    "../../../../../src/PaperDotNet.Host",
    "PaperDotNet.Host.csproj",
    "0")]

namespace PaperDotNet.Performance;

/// <summary>
/// The real host, with no test doubles. SQLite is a temporary file (WAL, as in production).
/// PostgreSQL is a private database on <c>PAPERDOTNET_TEST_POSTGRES</c>, or a Testcontainers server.
/// </summary>
internal sealed class PerfHost : WebApplicationFactory<global::Program>
{
    public const string Tenant = "default";
    public const string UserName = "admin";
    public const string Password = "perf-admin-password";

    private readonly string _provider;
    private readonly string _dataPath;
    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;
    private string? _sqliteFile;

    private PerfHost(string provider)
    {
        _provider = provider;
        _dataPath = Path.Combine(Path.GetTempPath(), $"pdn_perf_{Guid.NewGuid():N}");
    }

    public static async Task<PerfHost> StartAsync(string provider, CancellationToken cancellationToken)
    {
        var host = new PerfHost(provider);
        await host.PrepareDatabaseAsync(cancellationToken);
        _ = host.Server;
        return host;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseContentRoot(HostDirectory());
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(HostDirectory());
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", _provider == "sqlite" ? "Sqlite" : "PostgreSql");
        builder.UseSetting("ConnectionStrings:PaperDotNet", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:RequireHttps", "false");
        builder.UseSetting("Auth:AllowPasswordGrant", "true");
        builder.UseSetting("Tenancy:AllowHeader", "true");
        builder.UseSetting("Tenancy:DefaultTenant", Tenant);
        builder.UseSetting("Bootstrap:TenantIdentifier", Tenant);
        builder.UseSetting("Bootstrap:TenantName", "Performance");
        builder.UseSetting("Bootstrap:AdminUserName", UserName);
        builder.UseSetting("Bootstrap:AdminPassword", Password);
        builder.UseSetting("Storage:DataPath", _dataPath);
        builder.UseSetting("Jobs:SchedulerInterval", "01:00:00");
        builder.UseSetting("Search:DefaultMode", "Keyword");
        builder.UseSetting("RateLimit:PermitPerMinute", "1000000"); // Measures the server, not the per-user limit.
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }

        if (_sqliteFile is not null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { _sqliteFile, _sqliteFile + "-wal", _sqliteFile + "-shm" }.Where(File.Exists))
            {
                File.Delete(file);
            }
        }
    }

    private static string HostDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaperDotNet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("Repository root not found.")
            : Path.Combine(dir.FullName, "src", "PaperDotNet.Host");
    }

    private async Task PrepareDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_provider == "sqlite")
        {
            _sqliteFile = Path.Combine(Path.GetTempPath(), $"pdn_perf_{Guid.NewGuid():N}.db");
            _connectionString = $"Data Source={_sqliteFile}";
            return;
        }

        var server = Environment.GetEnvironmentVariable("PAPERDOTNET_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server))
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync(cancellationToken);
            server = _container.GetConnectionString();
        }

        var suffix = Guid.NewGuid().ToString("N");
        var role = $"pdn_perf_{suffix[..12]}";
        var password = $"pw_{suffix}";
        var database = $"pdn_perf_{suffix}";
        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync(cancellationToken);
            foreach (var sql in new[]
            {
                $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE",
                $"CREATE DATABASE {database} OWNER {role}",
            })
            {
                await using var command = new NpgsqlCommand(sql, admin);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        _connectionString = new NpgsqlConnectionStringBuilder(server)
        {
            Database = database,
            Username = role,
            Password = password,
        }.ConnectionString;
    }
}
