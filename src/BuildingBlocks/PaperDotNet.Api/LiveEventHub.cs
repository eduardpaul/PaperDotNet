using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Api;

/// <summary>
/// In-process fan-out of <see cref="LiveEvent"/>s to subscribers. Each subscriber has a small
/// bounded buffer; slow clients lose the oldest events rather than slowing publishers.
/// </summary>
public sealed class LiveEventHub : ILiveEvents
{
    private const int BufferSize = 256;
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public void Publish(LiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        foreach (var subscriber in _subscribers.Values)
        {
            if (subscriber.TenantId == liveEvent.TenantId && (liveEvent.UserId is null || liveEvent.UserId == subscriber.UserId))
            {
                subscriber.Channel.Writer.TryWrite(liveEvent);
            }
        }
    }

    public async IAsyncEnumerable<LiveEvent> SubscribeAsync(Guid tenantId, Guid userId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var subscriber = new Subscriber(tenantId, userId, Channel.CreateBounded<LiveEvent>(
            new BoundedChannelOptions(BufferSize) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }));
        _subscribers[id] = subscriber;
        try
        {
            await foreach (var liveEvent in subscriber.Channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return liveEvent;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private sealed record Subscriber(Guid TenantId, Guid UserId, Channel<LiveEvent> Channel);
}
