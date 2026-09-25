using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Api;

/// <summary>
/// Fan-out of <see cref="LiveEvent"/>s to the subscribers of this server. Each subscriber has a small bounded
/// buffer; slow clients lose the oldest events rather than slowing publishers. With an
/// <see cref="ILiveEventBackplane"/> (ADR-0026), events are also sent to the other servers, and events from them
/// are delivered here; local subscribers get local events right away (this server's own messages are skipped).
/// </summary>
public sealed partial class LiveEventHub : ILiveEvents
{
    private const int BufferSize = 256;
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ILiveEventBackplane? _backplane;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<LiveEventHub> _logger;
    private readonly Guid _server = Guid.CreateVersion7();

    public LiveEventHub(IOptions<JsonOptions> json, ILogger<LiveEventHub> logger, ILiveEventBackplane? backplane = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        _json = json.Value.SerializerOptions;
        _logger = logger;
        _backplane = backplane;
        if (backplane is not null)
        {
            backplane.Received += OnReceived;
        }
    }

    public void Publish(LiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        Deliver(liveEvent);
        if (_backplane is null)
        {
            return;
        }

        var message = JsonSerializer.Serialize(
            new Envelope(_server, liveEvent.Type, liveEvent.TenantId, liveEvent.UserId, JsonSerializer.SerializeToElement(liveEvent.Data, _json)), _json);
        if (Encoding.UTF8.GetByteCount(message) > _backplane.MaxMessageBytes)
        {
            LogTooLarge(liveEvent.Type);
            return;
        }

        _backplane.Send(message);
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

    private void Deliver(LiveEvent liveEvent)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            if (subscriber.TenantId == liveEvent.TenantId && (liveEvent.UserId is null || liveEvent.UserId == subscriber.UserId))
            {
                subscriber.Channel.Writer.TryWrite(liveEvent);
            }
        }
    }

    private void OnReceived(string message)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(message, _json);
        }
        catch (JsonException ex)
        {
            LogInvalid(ex);
            return;
        }

        if (envelope is null || envelope.Server == _server)
        {
            return;
        }

        Deliver(new LiveEvent(envelope.Type, envelope.TenantId, envelope.UserId, envelope.Data));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The live event {Type} is too large for other servers; only clients on this server get it.")]
    private partial void LogTooLarge(string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored an invalid live event message from the backplane.")]
    private partial void LogInvalid(Exception exception);

    private sealed record Subscriber(Guid TenantId, Guid UserId, Channel<LiveEvent> Channel);

    private sealed record Envelope(Guid Server, string Type, Guid TenantId, Guid? UserId, JsonElement Data);
}
