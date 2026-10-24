using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Persistence.Sqlite.Content;

/// <summary>Microsoft.Data.Sqlite starts a non-deferred write transaction; it already excludes other writers.</summary>
public sealed class SqliteContentMigrationLock : IContentMigrationLock
{
    public Task AcquireAsync(DbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("A source write transaction is required.");
        return Task.CompletedTask;
    }
}
