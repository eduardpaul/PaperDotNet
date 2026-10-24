using System.Collections.Concurrent;
using PaperDotNet.Content;
using PaperDotNet.Persistence.PostgreSql.Content;
using PaperDotNet.Persistence.Sqlite.Content;

namespace PaperDotNet.Host.Content;

/// <summary>Organization isolation is a database boundary; module migrations never touch these databases.</summary>
public sealed class ContentDatabaseFactory(IConfiguration configuration)
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<string>>> organizations = new();
    private bool PostgreSql => configuration["Database:Provider"]?.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<ContentDbContext> OpenAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var pending = organizations.GetOrAdd(tenantId, id => new Lazy<Task<string>>(() => InitializeAsync(id)));
        try
        {
            var connection = await pending.Value.WaitAsync(cancellationToken);
            return PostgreSql ? PostgreSqlContentDbContext.Open(connection) : SqliteContentDbContext.Open(connection);
        }
        catch { if (pending.IsValueCreated && pending.Value.IsFaulted) organizations.TryRemove(new KeyValuePair<Guid, Lazy<Task<string>>>(tenantId, pending)); throw; }
    }

    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct)
    {
        if (organizations.TryGetValue(tenantId, out var initialized) && initialized.IsValueCreated && initialized.Value.IsCompletedSuccessfully) return true;
        if (!PostgreSql) return File.Exists(System.IO.Path.Combine(Root, tenantId.ToString("N") + ".db"));
        return await PostgreSqlOrganizationDatabase.ExistsAsync(configuration.GetConnectionString("PaperDotNet")!, tenantId, ct);
    }

    private string Root => System.IO.Path.GetFullPath(configuration["Content:DataPath"] ?? System.IO.Path.Combine(configuration["Storage:DataPath"] ?? "data", "content"));

    /// <summary>Stopped restore only: removes a destination database absent from the restored backup.</summary>
    public async Task RemoveAsync(Guid tenantId, CancellationToken ct)
    {
        organizations.TryRemove(tenantId, out _);
        if (PostgreSql)
        {
            await PostgreSqlOrganizationDatabase.DeleteAsync(configuration["Content:ProvisioningConnectionString"]
                ?? configuration.GetConnectionString("PaperDotNet")!, tenantId, ct);
        }
        else
        {
            var path = System.IO.Path.Combine(Root, tenantId.ToString("N") + ".db");
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private async Task<string> InitializeAsync(Guid tenantId)
    {
        if (PostgreSql)
        {
            var application = configuration.GetConnectionString("PaperDotNet") ?? throw new InvalidOperationException("Database connection is required.");
            var connection = await PostgreSqlOrganizationDatabase.ProvisionAsync(application,
                configuration["Content:ProvisioningConnectionString"] ?? application, tenantId, CancellationToken.None);
            await using var db = PostgreSqlContentDbContext.Open(connection);
            await db.InitializeAsync();
            return connection;
        }
        var root = Root;
        Directory.CreateDirectory(root);
        var sqliteConnection = "Data Source=" + Path.Combine(root, tenantId.ToString("N") + ".db");
        await using var sqlite = SqliteContentDbContext.Open(sqliteConnection);
        await sqlite.InitializeAsync();
        return sqliteConnection;
    }
}
