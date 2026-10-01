using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Persistence;

/// <summary>Actions of the audit log (strings, no stored enums under Native AOT, ADR-0039).</summary>
public static class AuditActions
{
    public const string Created = "created";

    public const string Updated = "updated";

    /// <summary>Moved to the recycle bin (soft delete).</summary>
    public const string Deleted = "deleted";

    /// <summary>Restored from the recycle bin.</summary>
    public const string Restored = "restored";

    /// <summary>Deleted permanently.</summary>
    public const string Purged = "purged";
}

/// <summary>
/// One audit log record (LST-14): who changed which entity (<c>{module}.{Entity}</c>, e.g. <c>lists.ListItem</c>), when
/// and which properties (of an update, comma-separated), with the W3C trace id of the work for correlation with logs.
/// Values are not stored; item history lives in item versions.
/// </summary>
public sealed record AuditRecord(
    Guid Id, Guid TenantId, long AtUnixMs, Guid? UserId, string Action, string EntityType, Guid? EntityId, string? Properties, string? TraceId);

/// <summary>
/// Writes audit records in the transaction of the save that made the changes (<see cref="SaveChangesGuard"/>): plain SQL
/// on the context's connection, so every module's DbContext can write into the one audit table. One implementation per
/// database build.
/// </summary>
public interface IAuditLogWriter
{
    Task WriteAsync(DbContext db, IReadOnlyList<AuditRecord> records, CancellationToken cancellationToken);

    void Write(DbContext db, IReadOnlyList<AuditRecord> records);
}
