using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Messaging;

/// <summary>A background message that runs inside a tenant (and optionally as a user).</summary>
public interface ITenantMessage
{
    Guid TenantId { get; }

    string TenantIdentifier { get; }

    Guid? UserId { get; }
}

/// <summary>
/// Saves a DbContext and enqueues events/messages in the same transaction
/// (transactional outbox): either both are stored or neither is.
/// </summary>
public interface IOutbox
{
    /// <summary>Saves <paramref name="db"/>, publishing <paramref name="events"/> and <paramref name="messages"/> atomically.</summary>
    Task SaveChangesAsync(
        DbContext db,
        IReadOnlyCollection<IntegrationEvent> events,
        IReadOnlyCollection<ITenantMessage>? messages = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Sends background messages (optionally delayed) without a database transaction.</summary>
public interface IMessageScheduler
{
    Task ScheduleAsync(ITenantMessage message, TimeSpan delay, CancellationToken cancellationToken = default);
}
