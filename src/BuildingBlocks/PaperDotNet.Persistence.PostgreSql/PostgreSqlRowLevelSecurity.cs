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
/// setting <c>app.tenant_id</c>, which <see cref="TenantSessionInterceptor"/> sets
/// on every connection. Superusers bypass RLS, so run the app as an ordinary role.
/// </summary>
internal static partial class PostgreSqlRowLevelSecurity
{
    public const string Setting = "app.tenant_id";
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
            var condition = $"{Quote(column)} = nullif(current_setting('{Setting}', true), '')::uuid";
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

/// <summary>Sets the session's tenant (<c>app.tenant_id</c>) from the DbContext whenever a connection opens.</summary>
internal sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
    public static readonly TenantSessionInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Command(connection, eventData.Context);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, eventData.Context);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand Command(DbConnection connection, DbContext? context)
    {
        var tenant = (context as ITenantScopedDbContext)?.CurrentTenantId;
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT set_config('{PostgreSqlRowLevelSecurity.Setting}', @tenant, false)";
        command.Parameters.Add(new NpgsqlParameter("tenant", tenant?.ToString() ?? string.Empty));
        return command;
    }
}
