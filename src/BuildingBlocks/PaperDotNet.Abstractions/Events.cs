namespace PaperDotNet.Abstractions;

/// <summary>
/// Something that happened and was saved. Published with <c>IOutbox.SaveChangesAsync</c> (PaperDotNet.Messaging) in the
/// same transaction as the change; each subscriber (a Wolverine handler named <c>*Subscriber</c>) gets its own durable
/// message, retried on its own. Subscribers must be idempotent (use <see cref="EventId"/>).
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Ids.New();

    public required Guid TenantId { get; init; }

    public Guid? UserId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>How many event reactions led to this event (loop protection): a reaction publishes with depth + 1.</summary>
    public int Depth { get; init; }
}

/// <summary>Who makes a change: the tenant, the user (none for the organization, e.g. a workflow) and its causation depth.</summary>
public sealed record ChangeActor(Guid TenantId, Guid? UserId, int Depth = 0);
