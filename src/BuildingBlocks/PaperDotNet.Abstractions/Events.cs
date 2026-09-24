namespace PaperDotNet.Abstractions;

/// <summary>
/// A fact published after a change was committed. Delivered at least once, in
/// the background, to every <see cref="IEventSubscriber{TEvent}"/> (inside the
/// event's tenant). Transport-neutral: the host delivers it through its outbox.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Ids.New();

    public required Guid TenantId { get; init; }

    public required string TenantIdentifier { get; init; }

    public Guid? UserId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Handles an <see cref="IntegrationEvent"/> asynchronously (SharePoint-style
/// asynchronous "…ed" event). Must be idempotent: delivery is at least once and
/// failures are retried.
/// </summary>
public interface IEventSubscriber<in TEvent>
    where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
