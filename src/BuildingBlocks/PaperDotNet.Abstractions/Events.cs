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

    /// <summary>
    /// How many automatic reactions led to this event: 0 for a change a user or API client made,
    /// n + 1 for a change made while handling an event of depth n (see <see cref="EventCausation"/>).
    /// Automation uses it to stop chains that would never end.
    /// </summary>
    public int Depth { get; init; }
}

/// <summary>
/// The causation depth of the current scope (scoped service): code that reacts to an event sets
/// <see cref="Depth"/> to the event's depth + 1, and events published in the scope carry it.
/// </summary>
public sealed class EventCausation
{
    public int Depth { get; set; }
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
