using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence.PostgreSql.Content;
using PaperDotNet.Persistence.Sqlite.Content;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Host.Content;

/// <summary>Database-consistent organization snapshots are captured before backup enumerates their immutable blob files.</summary>
internal sealed class ContentBackupFolder(ContentDatabaseFactory factory, ITenantDirectory tenants, IConfiguration configuration) : IBackupFolder
{
    public string Name => "content-databases";
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdn-content-backup-" + Guid.NewGuid().ToString("N"));
    private bool PostgreSql => configuration["Database:Provider"]?.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) == true;
    private string Extension => PostgreSql ? ".pgdump" : ".sqlite";

    public async Task<IAsyncDisposable> FreezeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path);
        try
        {
            foreach (var tenant in await tenants.ListAsync(cancellationToken))
            {
                if (!await factory.ExistsAsync(tenant.Id, cancellationToken)) continue;
                await using var db = await factory.OpenAsync(tenant.Id, cancellationToken);
                var file = System.IO.Path.Combine(Path, tenant.Id.ToString("N") + Extension);
                if (PostgreSql) await PostgreSqlContentBackup.BackupAsync(db.Database.GetConnectionString()!, configuration, file, cancellationToken);
                else await SqliteContentBackup.BackupAsync(db.Database.GetConnectionString()!, file, cancellationToken);
            }
            return new Snapshot(Path);
        }
        catch { Directory.Delete(Path, true); throw; }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var restoredTenants = await tenants.ListAsync(cancellationToken);
        foreach (var tenant in restoredTenants)
        {
            if (!File.Exists(System.IO.Path.Combine(Path, tenant.Id.ToString("N") + Extension)) && await factory.ExistsAsync(tenant.Id, cancellationToken))
                await factory.RemoveAsync(tenant.Id, cancellationToken);
        }
        foreach (var file in Directory.EnumerateFiles(Path, "*" + Extension))
        {
            if (!Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(file), "N", out var tenantId))
                throw new InvalidOperationException("Invalid content database snapshot name.");
            if (!restoredTenants.Any(x => x.Id == tenantId))
                throw new InvalidOperationException("A content snapshot does not belong to a restored tenant.");
            await using var db = await factory.OpenAsync(tenantId, cancellationToken);
            if (PostgreSql) await PostgreSqlContentBackup.RestoreAsync(db.Database.GetConnectionString()!, configuration, file, cancellationToken);
            else await SqliteContentBackup.RestoreAsync(db.Database.GetConnectionString()!, file, cancellationToken);
        }
    }

    private sealed class Snapshot(string path) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { if (Directory.Exists(path)) Directory.Delete(path, true); return ValueTask.CompletedTask; }
    }
}
