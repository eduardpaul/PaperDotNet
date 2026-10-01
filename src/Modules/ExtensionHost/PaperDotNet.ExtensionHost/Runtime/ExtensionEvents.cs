using PaperDotNet.Abstractions;
using PaperDotNet.Extensions;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.ExtensionHost.Runtime;

/// <summary>
/// Delivers list events to the extensions' subscribers (<see cref="IEventSubscriber{TEvent}"/>) in tenants that
/// enabled them. The Wolverine handlers are generated ahead of time (ADR-0039), so extensions subscribe through this
/// one host subscriber per event type instead of handlers of their own.
/// </summary>
public sealed partial class ExtensionEvents(IServiceProvider services, IExtensionState state, ILogger<ExtensionEvents> logger)
{
    /// <summary>The events extensions can subscribe to.</summary>
    public static readonly IReadOnlySet<Type> Supported = new HashSet<Type>
    {
        typeof(ItemAdded), typeof(ItemUpdated), typeof(ItemDeleted), typeof(ItemRestored), typeof(ItemPurged), typeof(ListCreated), typeof(ListDeleted),
    };

    /// <summary>
    /// Runs every subscriber of the event whose extension is enabled in the event's tenant. A failing subscriber does not
    /// stop the others; the message is retried when one failed (subscribers are idempotent).
    /// </summary>
    public async Task DispatchAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IntegrationEvent
    {
        List<Exception> failures = [];
        foreach (var subscription in services.GetServices<ExtensionSubscription<TEvent>>())
        {
            if (!await state.IsEnabledAsync(integrationEvent.TenantId, subscription.ExtensionId, cancellationToken))
            {
                continue;
            }

            try
            {
                await subscription.Subscriber.HandleAsync(integrationEvent, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SubscriberFailed(logger, exception, subscription.Name, subscription.ExtensionId, typeof(TEvent).Name);
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscriber {Subscriber} of extension {Extension} failed for {Event}.")]
    private static partial void SubscriberFailed(ILogger logger, Exception exception, string subscriber, string extension, string @event);
}

/// <summary>Wolverine handlers of the list events for extensions (generated ahead of time).</summary>
public static class ExtensionEventsSubscriber
{
    public static Task Handle(ItemAdded e, ExtensionEvents events, CancellationToken cancellationToken) => events.DispatchAsync(e, cancellationToken);

    public static Task Handle(ItemUpdated e, ExtensionEvents events, CancellationToken cancellationToken) => events.DispatchAsync(e, cancellationToken);

    public static Task Handle(ItemDeleted e, ExtensionEvents events, CancellationToken cancellationToken) => events.DispatchAsync(e, cancellationToken);

    public static Task Handle(ItemRestored e, ExtensionEvents events, CancellationToken cancellationToken) => events.DispatchAsync(e, cancellationToken);

    public static Task Handle(ItemPurged e, ExtensionEvents events, CancellationToken cancellationToken) => events.DispatchAsync(e, cancellationToken);

    public static Task Handle(ListCreated e, ExtensionEvents events, CancellationToken cancellationToken) => events.DispatchAsync(e, cancellationToken);

    public static Task Handle(ListDeleted e, ExtensionEvents events, CancellationToken cancellationToken) => events.DispatchAsync(e, cancellationToken);
}
