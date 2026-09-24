namespace PaperDotNet.Abstractions;

/// <summary>
/// A notification for connected clients (API-07), e.g. <c>operation</c> or <c>document.processing</c>.
/// Delivered to <see cref="UserId"/> in <see cref="TenantId"/>, or to every user of the tenant when null.
/// </summary>
public sealed record LiveEvent(string Type, Guid TenantId, Guid? UserId, object Data);

/// <summary>
/// Live events for connected clients (<c>GET /v1.0/me/events</c>, server-sent events). Best effort and
/// in-process: clients reconnect and re-read state; durable reactions use integration events.
/// </summary>
public interface ILiveEvents
{
    void Publish(LiveEvent liveEvent);

    /// <summary>Events for <paramref name="userId"/> in <paramref name="tenantId"/> until cancelled.</summary>
    IAsyncEnumerable<LiveEvent> SubscribeAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}
