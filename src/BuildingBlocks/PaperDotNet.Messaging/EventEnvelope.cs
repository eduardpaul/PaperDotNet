using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Messaging;

/// <summary>
/// Transport message for one <see cref="IntegrationEvent"/>. Events travel as
/// JSON so subscribers never depend on the transport.
/// </summary>
public sealed record EventEnvelope(string EventType, string Payload, Guid TenantId, string TenantIdentifier);

/// <summary>Known integration event types, by stable name.</summary>
public sealed class EventTypeRegistry(IEnumerable<EventTypeRegistration> registrations)
{
    private readonly FrozenDictionary<string, Type> _byName = registrations.ToFrozenDictionary(r => r.Name, r => r.Type, StringComparer.Ordinal);

    public static string NameOf(Type type) => type.FullName ?? type.Name;

    public Type? Find(string name) => _byName.GetValueOrDefault(name);
}

public sealed record EventTypeRegistration(string Name, Type Type);

/// <summary>Delivers an <see cref="EventEnvelope"/> to all subscribers, inside the event's tenant.</summary>
public sealed class EventDispatcher(ITenantScopeFactory tenantScopes, EventTypeRegistry registry)
{
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, IntegrationEvent, CancellationToken, Task>> Dispatchers = new();

    public async Task DispatchAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var type = registry.Find(envelope.EventType)
            ?? throw new InvalidOperationException($"Unknown integration event type '{envelope.EventType}'.");
        var integrationEvent = (IntegrationEvent)JsonSerializer.Deserialize(envelope.Payload, type, MessagingJson.Options)!;

        await using var scope = tenantScopes.CreateScope(envelope.TenantId, envelope.TenantIdentifier, integrationEvent.UserId);
        var dispatch = Dispatchers.GetOrAdd(type, static t => (Func<IServiceProvider, IntegrationEvent, CancellationToken, Task>)
            Delegate.CreateDelegate(
                typeof(Func<IServiceProvider, IntegrationEvent, CancellationToken, Task>),
                typeof(EventDispatcher).GetMethod(nameof(DispatchTypedAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(t)));
        await dispatch(scope.ServiceProvider, integrationEvent, cancellationToken);
    }

    private static async Task DispatchTypedAsync<TEvent>(IServiceProvider services, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IntegrationEvent
    {
        foreach (var subscriber in services.GetServices<IEventSubscriber<TEvent>>())
        {
            await subscriber.HandleAsync((TEvent)integrationEvent, cancellationToken);
        }
    }
}

internal static class MessagingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
