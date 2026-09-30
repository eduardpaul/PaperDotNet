using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Core.Messaging;

/// <summary>
/// Something that happened and was saved. Published with <see cref="IOutbox.SaveChangesAsync"/> in the same
/// transaction as the change; each subscriber (a Wolverine handler) gets its own durable message, retried on its own.
/// Subscribers must be idempotent (use <see cref="EventId"/>).
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

/// <summary>
/// Saves a DbContext and enqueues events in the same transaction (transactional outbox): either both are stored or
/// neither is.
/// </summary>
public interface IOutbox
{
    Task SaveChangesAsync(DbContext db, IReadOnlyCollection<IntegrationEvent> events, CancellationToken cancellationToken = default);
}
