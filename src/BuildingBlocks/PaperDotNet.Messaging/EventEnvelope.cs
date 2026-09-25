using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Messaging;

/// <summary>
/// Transport message for one <see cref="IntegrationEvent"/> and one of its subscribers (named by
/// <see cref="Subscriber"/>). Events travel as JSON so subscribers never depend on the transport.
/// </summary>
public sealed record EventEnvelope(string EventType, string Payload, Guid TenantId, string TenantIdentifier, string Subscriber);

/// <summary>Known integration event types, by stable name.</summary>
public sealed class EventTypeRegistry(IEnumerable<EventTypeRegistration> registrations)
{
    private readonly FrozenDictionary<string, Type> _byName = registrations.ToFrozenDictionary(r => r.Name, r => r.Type, StringComparer.Ordinal);

    public static string NameOf(Type type) => type.FullName ?? type.Name;

    public Type? Find(string name) => _byName.GetValueOrDefault(name);
}

public sealed record EventTypeRegistration(string Name, Type Type);

/// <summary>Names of the subscribers of each event type (from <see cref="EventSubscriberRegistration"/>s).</summary>
public sealed class EventSubscriberRegistry(IEnumerable<EventSubscriberRegistration> registrations)
{
    private readonly FrozenDictionary<Type, string[]> _byType = registrations
        .GroupBy(r => r.EventType)
        .ToFrozenDictionary(g => g.Key, g => g.Select(r => r.Name).Distinct(StringComparer.Ordinal).ToArray());

    public IReadOnlyList<string> For(Type eventType) => _byType.GetValueOrDefault(eventType) ?? [];
}

/// <summary>Delivers an <see cref="EventEnvelope"/> to its subscriber, inside the event's tenant.</summary>
public sealed partial class EventDispatcher(ITenantScopeFactory tenantScopes, EventTypeRegistry registry, ILogger<EventDispatcher> logger)
{
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, IntegrationEvent, string, CancellationToken, Task<bool>>> Dispatchers = new();

    public async Task DispatchAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var type = registry.Find(envelope.EventType)
            ?? throw new InvalidOperationException($"Unknown integration event type '{envelope.EventType}'.");
        var integrationEvent = (IntegrationEvent)JsonSerializer.Deserialize(envelope.Payload, type, MessagingJson.Options)!;

        await using var scope = tenantScopes.CreateScope(envelope.TenantId, envelope.TenantIdentifier, integrationEvent.UserId);
        var dispatch = Dispatchers.GetOrAdd(type, static t => (Func<IServiceProvider, IntegrationEvent, string, CancellationToken, Task<bool>>)
            Delegate.CreateDelegate(
                typeof(Func<IServiceProvider, IntegrationEvent, string, CancellationToken, Task<bool>>),
                typeof(EventDispatcher).GetMethod(nameof(DispatchTypedAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(t)));
        if (!await dispatch(scope.ServiceProvider, integrationEvent, envelope.Subscriber, cancellationToken))
        {
            // The subscriber was removed (e.g. by an update) after the message was queued.
            LogUnknownSubscriber(envelope.Subscriber, envelope.EventType);
        }
    }

    private static async Task<bool> DispatchTypedAsync<TEvent>(IServiceProvider services, IntegrationEvent integrationEvent, string subscriber, CancellationToken cancellationToken)
        where TEvent : IntegrationEvent
    {
        if (services.GetKeyedService<IEventSubscriber<TEvent>>(subscriber) is not { } handler)
        {
            return false;
        }

        await handler.HandleAsync((TEvent)integrationEvent, cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {EventType} for the unknown subscriber {Subscriber}.")]
    private partial void LogUnknownSubscriber(string subscriber, string eventType);
}

internal static class MessagingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
