using Microsoft.Data.Sqlite;
using Npgsql;
using Testcontainers.PostgreSql;

namespace PaperDotNet.Performance;

internal sealed record DatabaseMetadata(string Provider, string Version, string Deployment, IReadOnlyDictionary<string, string> Durability);

/// <summary>Owns only the randomly named database/role and temporary directory created by this run.</summary>
internal sealed class PerfDatabase : IAsyncDisposable
{
    private PostgreSqlContainer? container;
    private string? adminConnection;
    private string? role;
    private string? database;
    private bool roleCreated;
    private bool databaseCreated;
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), $"pdn_perf_{Guid.NewGuid():N}");
    public string ConnectionString { get; private set; } = string.Empty;
    public DatabaseMetadata Metadata { get; private set; } = null!;

    public static async Task<PerfDatabase> CreateAsync(string provider, CancellationToken ct)
    {
        var instance = new PerfDatabase();
        Directory.CreateDirectory(instance.DataPath);
        try { await instance.PrepareAsync(provider, ct); return instance; }
        catch { await instance.DisposeAsync(); throw; }
    }

    private async Task PrepareAsync(string provider, CancellationToken ct)
    {
        if (provider == "sqlite")
        {
            ConnectionString = $"Data Source={Path.Combine(DataPath, "app.db")}";
            // Probe the linked engine without creating the file. EF's existence check opens
            // existing files read-only, before the application's WAL interceptor can initialize them.
            await using var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync(ct);
            await using var version = sqlite.CreateCommand();
            version.CommandText = "SELECT sqlite_version()";
            Metadata = new DatabaseMetadata(provider, (string)(await version.ExecuteScalarAsync(ct))!, "temporary local file",
                new Dictionary<string, string> { ["journal_mode"] = "WAL", ["synchronous"] = "NORMAL (application configuration)" });
            return;
        }

        adminConnection = Environment.GetEnvironmentVariable("PAPERDOTNET_TEST_POSTGRES");
        var deployment = "external server";
        if (string.IsNullOrWhiteSpace(adminConnection))
        {
            var image = Environment.GetEnvironmentVariable("PERF_POSTGRES_IMAGE") ?? "postgres:17-alpine";
            var memoryText = Environment.GetEnvironmentVariable("PERF_POSTGRES_MEMORY_MIB") ?? "512";
            if (!int.TryParse(memoryText, out var memoryMiB) || memoryMiB is < 128 or > 16384)
            {
                throw new ArgumentException("PERF_POSTGRES_MEMORY_MIB must be between 128 and 16384.");
            }
            container = new PostgreSqlBuilder(image)
                .WithCreateParameterModifier(p =>
                {
                    var limits = p.HostConfig ??= new();
                    limits.Memory = (long)memoryMiB * 1024 * 1024;
                    limits.MemorySwap = limits.Memory;
                    limits.NanoCPUs = 1_000_000_000;
                })
                // PostgreSqlBuilder already supplies the executable. These switches are
                // appended and override its performance-oriented fsync default.
                .WithCommand("-c", "fsync=on", "-c", "synchronous_commit=on", "-c", "full_page_writes=on").Build();
            await container.StartAsync(ct);
            adminConnection = container.GetConnectionString();
            deployment = $"Testcontainers: {image}; {memoryMiB} MiB hard memory limit, no swap, 1 CPU quota";
        }

        var suffix = Guid.NewGuid().ToString("N");
        role = $"pdn_perf_{suffix[..12]}";
        database = $"pdn_perf_{suffix}";
        var password = $"pw_{suffix}";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(ct);
        await using (var command = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE", admin))
        {
            await command.ExecuteNonQueryAsync(ct);
            roleCreated = true;
        }

        await using (var command = new NpgsqlCommand($"CREATE DATABASE {database} OWNER {role}", admin))
        {
            await command.ExecuteNonQueryAsync(ct);
            databaseCreated = true;
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database, Username = role, Password = password }.ConnectionString;
        await using var probe = new NpgsqlConnection(ConnectionString);
        await probe.OpenAsync(ct);
        var durability = new Dictionary<string, string>();
        foreach (var setting in new[] { "fsync", "synchronous_commit", "full_page_writes" })
        {
            await using var command = new NpgsqlCommand($"SHOW {setting}", probe);
            durability[setting] = (string)(await command.ExecuteScalarAsync(ct))!;
        }

        Metadata = new DatabaseMetadata(provider, probe.PostgreSqlVersion.ToString(), deployment, durability);
        if (durability.Values.Any(value => value != "on"))
        {
            throw new InvalidOperationException("Baseline requires PostgreSQL fsync, synchronous_commit and full_page_writes to be on.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (adminConnection is not null && (databaseCreated || roleCreated))
            {
                await using var admin = new NpgsqlConnection(adminConnection);
                await admin.OpenAsync();
                if (databaseCreated)
                {
                    await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
                    await drop.ExecuteNonQueryAsync();
                }

                if (roleCreated)
                {
                    await using var drop = new NpgsqlCommand($"DROP ROLE {role}", admin);
                    await drop.ExecuteNonQueryAsync();
                }
            }
        }
        finally
        {
            if (container is not null) { await container.DisposeAsync(); }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DataPath)) { Directory.Delete(DataPath, recursive: true); }
        }
    }
}
