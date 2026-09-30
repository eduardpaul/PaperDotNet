using PaperDotNet.Abstractions;

namespace PaperDotNet.Audit.Data;

#pragma warning disable CA1852 // Not sealed: EF Core precompiled materializers (ADR-0039).

/// <summary>One recorded event. The id is the event's id, so a redelivered event is recorded once.</summary>
public class AuditEntry : ITenantOwned
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

#pragma warning restore CA1852
