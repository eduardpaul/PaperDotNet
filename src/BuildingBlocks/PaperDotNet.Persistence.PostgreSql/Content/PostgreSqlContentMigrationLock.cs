using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Persistence.PostgreSql.Content;

public sealed class PostgreSqlContentMigrationLock : IContentMigrationLock
{
    public async Task AcquireAsync(DbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("A source write transaction is required.");
        var key = BinaryPrimitives.ReadInt64LittleEndian(tenantId.ToByteArray());
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }
}
