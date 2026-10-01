using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace PaperDotNet.Persistence.Sqlite;

/// <summary>
/// Inserts audit records into <c>audit_log</c> (the Audit module's table) on the saving context's connection and
/// transaction. Ids are stored as EF Core stores them on SQLite (upper-case text).
/// </summary>
internal sealed class SqliteAuditLogWriter : IAuditLogWriter
{
    private const int RowsPerCommand = 200;

    public async Task WriteAsync(DbContext db, IReadOnlyList<AuditRecord> records, CancellationToken cancellationToken)
    {
        for (var start = 0; start < records.Count; start += RowsPerCommand)
        {
            await using var command = Command(db, records, start);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public void Write(DbContext db, IReadOnlyList<AuditRecord> records)
    {
        for (var start = 0; start < records.Count; start += RowsPerCommand)
        {
            using var command = Command(db, records, start);
            command.ExecuteNonQuery();
        }
    }

    private static DbCommand Command(DbContext db, IReadOnlyList<AuditRecord> records, int start)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var sql = new StringBuilder("INSERT INTO \"audit_log\" (\"Id\", \"TenantId\", \"AtUnixMs\", \"UserId\", \"Action\", \"EntityType\", \"EntityId\", \"Properties\", \"TraceId\") VALUES ");
        var end = Math.Min(records.Count, start + RowsPerCommand);
        for (var i = start; i < end; i++)
        {
            var record = records[i];
            sql.Append(i == start ? "(" : ", (");
            Add(command, sql, Text(record.Id));
            Add(command, sql, Text(record.TenantId), ", ");
            Add(command, sql, record.AtUnixMs, ", ");
            Add(command, sql, record.UserId is { } user ? Text(user) : null, ", ");
            Add(command, sql, record.Action, ", ");
            Add(command, sql, record.EntityType, ", ");
            Add(command, sql, record.EntityId is { } entity ? Text(entity) : null, ", ");
            Add(command, sql, record.Properties, ", ");
            Add(command, sql, record.TraceId, ", ");
            sql.Append(')');
        }

        command.CommandText = sql.ToString();
        return command;
    }

    private static void Add(DbCommand command, StringBuilder sql, object? value, string separator = "")
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@p" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        sql.Append(separator).Append(parameter.ParameterName);
    }

    private static string Text(Guid id) => id.ToString().ToUpperInvariant();
}
