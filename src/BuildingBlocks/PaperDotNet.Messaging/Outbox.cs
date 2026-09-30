using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Messaging;

/// <summary>
/// Saves a DbContext and enqueues events (and other messages, e.g. commands to background handlers) in the same
/// transaction (transactional outbox): either all are stored or none is.
/// </summary>
public interface IOutbox
{
    Task SaveChangesAsync(DbContext db, IReadOnlyCollection<IntegrationEvent> events, CancellationToken cancellationToken = default) =>
        SaveChangesAsync(db, events, [], cancellationToken);

    Task SaveChangesAsync(DbContext db, IReadOnlyCollection<IntegrationEvent> events, IReadOnlyCollection<object> messages, CancellationToken cancellationToken = default);
}
