using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence.PostgreSql;

/// <summary>
/// Live events between servers over PostgreSQL LISTEN/NOTIFY (ADR-0026). One connection per server listens on
/// <see cref="Channel"/> and reconnects after failures; messages are sent in order by a background loop with
/// <c>pg_notify</c>. Best effort: notifications sent while a server is reconnecting do not reach it. Needs a
/// session-level connection (not a transaction-pooling proxy such as PgBouncer in transaction mode).
/// </summary>
public sealed partial class PostgreSqlLiveEventBackplane(NpgsqlDataSource dataSource, ILogger<PostgreSqlLiveEventBackplane> logger)
    : BackgroundService, ILiveEventBackplane
{
    public const string Channel = "paperdotnet_live_events";

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Channel<string> _outgoing = System.Threading.Channels.Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>PostgreSQL rejects NOTIFY payloads of 8000 bytes or more.</summary>
    public int MaxMessageBytes => 7900;

    public event Action<string>? Received;

    /// <summary>Completes once this server listens (for tests and startup ordering).</summary>
    public Task Listening => _listening.Task;

    public void Send(string message) => _outgoing.Writer.TryWrite(message);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ListenAsync(stoppingToken), SendAsync(stoppingToken));

    private async Task ListenAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(ct);
                connection.Notification += (_, e) => Dispatch(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(ct);
                }

                _listening.TrySetResult();
                backoff = TimeSpan.FromSeconds(1);
                while (!ct.IsCancellationRequested)
                {
                    await connection.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or TimeoutException)
            {
                LogListenFailed(ex, backoff);
                try
                {
                    await Task.Delay(backoff, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    private async Task SendAsync(CancellationToken ct)
    {
        await foreach (var message in _outgoing.Reader.ReadAllAsync(ct).WithCancellation(ct))
        {
            try
            {
                await using var command = dataSource.CreateCommand("SELECT pg_notify($1, $2)");
                command.Parameters.Add(new NpgsqlParameter { Value = Channel });
                command.Parameters.Add(new NpgsqlParameter { Value = message });
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or TimeoutException)
            {
                // Best effort: the event is dropped; clients re-read state when they reconnect.
                LogSendFailed(ex);
            }
        }
    }

    private void Dispatch(string payload)
    {
        try
        {
            Received?.Invoke(payload);
        }
#pragma warning disable CA1031 // A failing receiver must not stop listening.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReceiveFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listening for live events failed; retrying in {Backoff}.")]
    private partial void LogListenFailed(Exception exception, TimeSpan backoff);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending a live event to other servers failed.")]
    private partial void LogSendFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivering a live event from another server failed.")]
    private partial void LogReceiveFailed(Exception exception);
}
