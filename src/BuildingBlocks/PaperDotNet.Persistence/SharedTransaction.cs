using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace PaperDotNet.Persistence;

/// <summary>Temporarily enlists a module context in another module's transaction, without owning its connection.</summary>
public static class SharedTransaction
{
    public static async Task RunAsync(DbContext db, DbTransaction transaction, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        // Use the configured string, not an opened connection's potentially redacted string.
        // Data-source providers have a null configured string and recreate from their data source.
        var configuredConnectionString = db.GetService<IDbContextOptions>().Extensions
            .OfType<RelationalOptionsExtension>().Single().ConnectionString;
        db.Database.SetDbConnection(transaction.Connection!, contextOwnsConnection: false);
        try
        {
            await db.Database.UseTransactionAsync(transaction, ct);
            await action(ct);
        }
        finally
        {
            await db.Database.UseTransactionAsync(null, CancellationToken.None);
            // Switching disposes the original EF-owned connection; recreate it on subsequent use.
            db.Database.SetDbConnection(null);
            if (configuredConnectionString is not null)
            {
                db.Database.SetConnectionString(configuredConnectionString);
            }
        }
    }
}
