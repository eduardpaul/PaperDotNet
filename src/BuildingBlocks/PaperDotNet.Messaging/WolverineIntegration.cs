using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;

namespace PaperDotNet.Messaging;

/// <summary>Wolverine handler for event envelopes (discovered by convention).</summary>
public static class EventEnvelopeHandler
{
    public static Task Handle(EventEnvelope envelope, EventDispatcher dispatcher, CancellationToken cancellationToken) =>
        dispatcher.DispatchAsync(envelope, cancellationToken);
}

internal sealed class WolverineOutbox(IDbContextOutbox outbox, TimeProvider time) : IOutbox
{
    public async Task SaveChangesAsync(
        DbContext db, IReadOnlyCollection<IntegrationEvent> events, IReadOnlyCollection<ITenantMessage>? messages = null, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0 && (messages is null || messages.Count == 0))
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        outbox.Enroll(db);
        foreach (var integrationEvent in events)
        {
            var stamped = integrationEvent.OccurredAt == default ? integrationEvent with { OccurredAt = time.GetUtcNow() } : integrationEvent;
            await outbox.PublishAsync(new EventEnvelope(
                EventTypeRegistry.NameOf(stamped.GetType()),
                JsonSerializer.Serialize(stamped, stamped.GetType(), MessagingJson.Options),
                stamped.TenantId,
                stamped.TenantIdentifier));
        }

        foreach (var message in messages ?? [])
        {
            await outbox.PublishAsync(message);
        }

        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }
}

internal sealed class WolverineMessageScheduler(IMessageBus bus) : IMessageScheduler
{
    public async Task ScheduleAsync(ITenantMessage message, TimeSpan delay, CancellationToken cancellationToken = default) =>
        await bus.ScheduleAsync(message, delay);
}

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Adds reliable messaging: Wolverine with durable local queues, the EF Core
    /// transactional outbox, retries and dead-lettering. <paramref name="configureStorage"/>
    /// picks the message storage matching the database provider.
    /// </summary>
    public static IServiceCollection AddPaperDotNetMessaging(
        this IServiceCollection services, Action<WolverineOptions> configureStorage, IEnumerable<System.Reflection.Assembly> handlerAssemblies)
    {
        services.AddSingleton<EventTypeRegistry>();
        services.AddSingleton<EventDispatcher>();
        services.AddScoped<IOutbox, WolverineOutbox>();
        services.AddScoped<IMessageScheduler, WolverineMessageScheduler>();

        services.AddWolverine(options =>
        {
            configureStorage(options);
            options.UseEntityFrameworkCoreTransactions();
            options.Policies.UseDurableLocalQueues();
            options.Discovery.IncludeAssembly(typeof(EventEnvelopeHandler).Assembly);
            foreach (var assembly in handlerAssemblies)
            {
                options.Discovery.IncludeAssembly(assembly);
            }

            // Quick retries for transient failures, then delayed retries, then the dead-letter queue.
            options.Policies.OnAnyException()
                .RetryWithCooldown(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2))
                .Then.ScheduleRetry(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5))
                .Then.MoveToErrorQueue();
        });
        return services;
    }

    /// <summary>Registers an integration event type so it can be delivered to subscribers.</summary>
    public static IServiceCollection AddIntegrationEvent<TEvent>(this IServiceCollection services)
        where TEvent : IntegrationEvent
    {
        services.AddSingleton(new EventTypeRegistration(EventTypeRegistry.NameOf(typeof(TEvent)), typeof(TEvent)));
        return services;
    }

    /// <summary>Registers a subscriber for an integration event.</summary>
    public static IServiceCollection AddEventSubscriber<TEvent, TSubscriber>(this IServiceCollection services)
        where TEvent : IntegrationEvent
        where TSubscriber : class, IEventSubscriber<TEvent>
    {
        services.AddScoped<IEventSubscriber<TEvent>, TSubscriber>();
        return services;
    }
}
