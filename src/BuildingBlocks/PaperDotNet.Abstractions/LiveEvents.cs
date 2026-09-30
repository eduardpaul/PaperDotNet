using System.Text.Json.Nodes;

namespace PaperDotNet.Abstractions;

/// <summary>
/// A notification for connected clients (API-07), e.g. <c>operation</c> or <c>document.processing</c>.
/// Delivered to <see cref="UserId"/> in <see cref="TenantId"/>, or to every user of the tenant when null.
/// The data is JSON, so it serializes without reflection (ADR-0039).
/// </summary>
public sealed record LiveEvent(string Type, Guid TenantId, Guid? UserId, JsonNode Data)
{
    /// <summary>
    /// When set, only users with one of these principals get the event (see <see cref="IPrincipalSet"/>), e.g. the
    /// principals of an item's permission scope (ADR-0035).
    /// </summary>
    public IReadOnlyCollection<Guid>? Audience { get; init; }
}

/// <summary>
/// Who a user is for access checks (ADR-0035): their id, their groups (including groups that contain them) and their
/// workspace roles. Without an implementation, a user's only principal is their id.
/// </summary>
public interface IPrincipalSet
{
    Task<IReadOnlyList<Guid>> GetPrincipalsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Live events for connected clients (<c>GET /v1.0/me/events</c>, server-sent events). Best effort: clients
/// reconnect and re-read state; durable reactions use integration events. With several servers, events reach
/// clients on every server through the database's <see cref="ILiveEventBackplane"/> (ADR-0026).
/// </summary>
public interface ILiveEvents
{
    void Publish(LiveEvent liveEvent);

    /// <summary>Events for <paramref name="userId"/> in <paramref name="tenantId"/> until cancelled.</summary>
    IAsyncEnumerable<LiveEvent> SubscribeAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Carries live events between servers (ADR-0026), implemented by a database provider (PostgreSQL: LISTEN/NOTIFY).
/// Without one (SQLite, a single server) events stay in the process. Best effort: messages may be lost while a
/// server reconnects.
/// </summary>
public interface ILiveEventBackplane
{
    /// <summary>Largest message (UTF-8 bytes) the backplane carries; larger events stay on the server that raised them.</summary>
    int MaxMessageBytes { get; }

    /// <summary>Queues a message for all servers (including this one); never blocks or throws.</summary>
    void Send(string message);

    /// <summary>Raised for every message received from any server (including this one's).</summary>
    event Action<string>? Received;
}
