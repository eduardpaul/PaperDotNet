using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence;

public enum AuditAction
{
    Created,
    Updated,

    /// <summary>Moved to the recycle bin (soft delete).</summary>
    Deleted,

    /// <summary>Restored from the recycle bin.</summary>
    Restored,

    /// <summary>Deleted permanently.</summary>
    Purged,
}

/// <summary>
/// One append-only audit record: who changed which entity, when and which
/// properties (LST-14). Every module DbContext gets an <c>audit_log</c> table in its
/// schema; <see cref="AuditingInterceptor"/> writes records in the same transaction
/// as the change. Values are not stored: item history lives in item versions.
/// </summary>
[NotAudited]
public sealed class AuditEntry : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>The acting user; null for system work (jobs, migrations).</summary>
    public Guid? UserId { get; set; }

    public AuditAction Action { get; set; }

    /// <summary><c>{schema}.{Entity}</c>, e.g. <c>lists.ListItem</c>.</summary>
    public required string EntityType { get; set; }

    public Guid? EntityId { get; set; }

    /// <summary>Changed properties (updates only).</summary>
    public List<string> Properties { get; set; } = [];

    /// <summary>W3C trace id of the request or background job, for correlation with logs.</summary>
    public string? TraceId { get; set; }
}
