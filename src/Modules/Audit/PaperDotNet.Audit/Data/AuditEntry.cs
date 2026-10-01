using PaperDotNet.Abstractions;

namespace PaperDotNet.Audit.Data;

#pragma warning disable CA1852 // Not sealed: EF Core precompiled materializers (ADR-0039).

/// <summary>One recorded event. The id is the event's id, so a redelivered event is recorded once.</summary>
public class AuditEntry : ITenantOwned, INotAudited
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>Event name, e.g. <c>item.created</c>.</summary>
    public string Action { get; set; } = "";

    public Guid TargetId { get; set; }

    public Guid? ListId { get; set; }

    public string? Summary { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// One change of an entity of any module (LST-14), written by the save guard in the transaction of the change
/// (<c>IAuditLogWriter</c>); read here, never written through this context.
/// </summary>
public class AuditLogEntry : ITenantOwned, INotAudited
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public long AtUnixMs { get; set; }

    public Guid? UserId { get; set; }

    /// <summary><c>created</c>, <c>updated</c>, <c>deleted</c>, <c>restored</c> or <c>purged</c>.</summary>
    public string Action { get; set; } = "";

    /// <summary><c>{module}.{Entity}</c>, e.g. <c>lists.ListItem</c>.</summary>
    public string EntityType { get; set; } = "";

    public Guid? EntityId { get; set; }

    /// <summary>Changed properties of an update, comma-separated.</summary>
    public string? Properties { get; set; }

    public string? TraceId { get; set; }
}

#pragma warning restore CA1852
