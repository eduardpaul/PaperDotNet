using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
/// Handles an <see cref="IntegrationEvent"/> in the background. Each subscriber gets the event as its own
/// message (ADR-0023), so its retries and failures never affect other subscribers. Must be idempotent:
/// delivery is at least once and failures are retried. Register with
/// <see cref="EventSubscriberServiceCollectionExtensions.AddEventSubscriber{TEvent, TSubscriber}"/>.
/// </summary>
public interface IEventSubscriber<in TEvent>
    where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}

/// <summary>A registered subscriber of <see cref="EventType"/>; <see cref="Name"/> routes the event's messages to it.</summary>
public sealed record EventSubscriberRegistration(Type EventType, string Name);

public static class EventSubscriberServiceCollectionExtensions
{
    /// <summary>
    /// Registers a subscriber for an integration event. The subscriber is a scoped service (one instance per
    /// scope, also when it subscribes to several events); its name is its full type name.
    /// </summary>
    public static IServiceCollection AddEventSubscriber<TEvent, TSubscriber>(this IServiceCollection services)
        where TEvent : IntegrationEvent
        where TSubscriber : class, IEventSubscriber<TEvent>
    {
        services.TryAddScoped<TSubscriber>();
        return services.AddEventSubscriber<TEvent>(typeof(TSubscriber).FullName!, sp => sp.GetRequiredService<TSubscriber>());
    }

    /// <summary>Registers a subscriber created by <paramref name="factory"/> under a stable <paramref name="name"/>.</summary>
    public static IServiceCollection AddEventSubscriber<TEvent>(this IServiceCollection services, string name, Func<IServiceProvider, IEventSubscriber<TEvent>> factory)
        where TEvent : IntegrationEvent
    {
        services.AddKeyedScoped<IEventSubscriber<TEvent>>(name, (sp, _) => factory(sp));
        services.AddSingleton(new EventSubscriberRegistration(typeof(TEvent), name));
        return services;
    }
}
