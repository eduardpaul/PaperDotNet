using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Npgsql;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence.PostgreSql;

/// <summary>
/// Row-level security (defense in depth behind the EF tenant filter): every
/// tenant-owned table only shows and accepts rows of the tenant in the session
/// setting <c>app.tenant_id</c>, which <see cref="TenantCommandInterceptor"/> sends
/// with every command. Superusers bypass RLS, so run the app as an ordinary role.
/// Maintenance sessions (whole-database backup and restore, PLT-12) set
/// <see cref="MaintenanceSetting"/> to <c>on</c>; application code never sets it.
/// </summary>
internal static partial class PostgreSqlRowLevelSecurity
{
    public const string Setting = "app.tenant_id";

    /// <summary>Session setting that lets <c>pg_dump</c>/<c>pg_restore</c> see all tenants (set through <c>PGOPTIONS</c>).</summary>
    public const string MaintenanceSetting = "app.maintenance";
    private const string Policy = "pdn_tenant_isolation";

    /// <summary>Enables and forces RLS with the tenant policy on the context's tenant-owned tables (idempotent).</summary>
    public static async Task ApplyAsync(DbContext context, ILogger logger, CancellationToken ct)
    {
        foreach (var entityType in context.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType) && t.GetTableName() is not null))
        {
            var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
            var column = entityType.FindProperty(nameof(ITenantOwned.TenantId))?.GetColumnName(table);
            if (column is null)
            {
                continue;
            }

            var name = table.Schema is null ? Quote(table.Name) : $"{Quote(table.Schema)}.{Quote(table.Name)}";
            var condition = $"{Quote(column)} = nullif(current_setting('{Setting}', true), '')::uuid OR current_setting('{MaintenanceSetting}', true) = 'on'";
#pragma warning disable EF1002 // Identifiers come from the EF model, not from user input.
            await context.Database.ExecuteSqlRawAsync(
                $"""
                ALTER TABLE {name} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE {name} FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS {Policy} ON {name};
                CREATE POLICY {Policy} ON {name} USING ({condition}) WITH CHECK ({condition});
                """,
                ct);
#pragma warning restore EF1002
        }

        var superuser = await context.Database.SqlQueryRaw<bool>("SELECT rolsuper AS \"Value\" FROM pg_roles WHERE rolname = current_user").FirstOrDefaultAsync(ct);
        if (superuser)
        {
            LogSuperuser(logger);
        }
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connected to PostgreSQL as a superuser: row-level security is bypassed. Run PaperDotNet as an ordinary role that owns the database.")]
    private static partial void LogSuperuser(ILogger logger);
}

/// <summary>
/// Sends the tenant (<c>app.tenant_id</c>) of the DbContext with the commands that need it (ADR-0035), without the
/// extra round trip per connection open that EF's connection-per-query pattern made a round trip per query:
/// <list type="bullet">
/// <item>Queries and bulk updates outside a transaction get a <c>SET</c> statement in front, in the same round trip and
/// the same implicit transaction (so it stays right behind a transaction-mode pooler such as PgBouncer). <c>SET</c>
/// returns no rows, so readers start at the command's own result, and adds nothing to the rows affected.</item>
/// <item>Transactions get <c>SET LOCAL</c> when they start, and <c>SaveChanges</c> always runs in one: its batches
/// read the rows affected per statement, which a statement in front would shift.</item>
/// </list>
/// The tenant is a GUID, so it is written as a literal.
/// </summary>
internal sealed class TenantCommandInterceptor : DbCommandInterceptor
{
    public static readonly TenantCommandInterceptor Instance = new();

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Prefix(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Prefix(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Prefix(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Prefix(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Prefix(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Prefix(command, eventData);
        return ValueTask.FromResult(result);
    }

    internal static string Value(DbContext? context) =>
        (context as ITenantScopedDbContext)?.CurrentTenantId is { } id ? id.ToString("D") : string.Empty;

    private static void Prefix(DbCommand command, CommandEventData eventData)
    {
        // Inside a transaction the setting is already there (SET LOCAL at its start).
        if (command.Transaction is null && eventData.CommandSource is not (CommandSource.SaveChanges or CommandSource.Migrations))
        {
            command.CommandText = $"SET {PostgreSqlRowLevelSecurity.Setting} = '{Value(eventData.Context)}';\n{command.CommandText}";
        }
    }
}

/// <summary>Sets the tenant for the whole transaction when EF starts one (see <see cref="TenantCommandInterceptor"/>).</summary>
internal sealed class TenantTransactionInterceptor : DbTransactionInterceptor
{
    public static readonly TenantTransactionInterceptor Instance = new();

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = Command(connection, eventData.Context, result);
        command.ExecuteNonQuery();
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, eventData.Context, result);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    private static DbCommand Command(DbConnection connection, DbContext? context, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SET LOCAL {PostgreSqlRowLevelSecurity.Setting} = '{TenantCommandInterceptor.Value(context)}'";
        return command;
    }
}

/// <summary>Makes <c>SaveChanges</c> run in a transaction, so <see cref="TenantTransactionInterceptor"/> sets the tenant.</summary>
internal sealed class TenantSaveChangesInterceptor : SaveChangesInterceptor
{
    public static readonly TenantSaveChangesInterceptor Instance = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Always(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Always(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Always(DbContext? context)
    {
        if (context is not null)
        {
            context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        }
    }
}
