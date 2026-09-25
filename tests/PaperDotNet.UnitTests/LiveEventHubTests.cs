using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;

namespace PaperDotNet.UnitTests;

/// <summary>Live events between servers through a backplane (ADR-0026).</summary>
public sealed class LiveEventHubTests
{
    /// <summary>An in-memory stand-in for LISTEN/NOTIFY shared by several "servers".</summary>
    private sealed class Bus
    {
        public List<FakeBackplane> Servers { get; } = [];

        public FakeBackplane Join()
        {
            var backplane = new FakeBackplane(this);
            Servers.Add(backplane);
            return backplane;
        }
    }

    private sealed class FakeBackplane(Bus bus) : ILiveEventBackplane
    {
        public int MaxMessageBytes => 300;

        public int Sent { get; private set; }

        public event Action<string>? Received;

        public void Send(string message)
        {
            Sent++;
            foreach (var server in bus.Servers)
            {
                server.Received?.Invoke(message);
            }
        }

        public void Raise(string message) => Received?.Invoke(message);
    }

    private static LiveEventHub Hub(ILiveEventBackplane backplane)
    {
        var json = new JsonOptions();
        json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        return new LiveEventHub(Options.Create(json), NullLogger<LiveEventHub>.Instance, backplane);
    }

    private static async Task<List<LiveEvent>> CollectAsync(LiveEventHub hub, Guid tenant, Guid user, Action publish)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var received = new List<LiveEvent>();
        var reading = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in hub.SubscribeAsync(tenant, user, cts.Token))
                {
                    received.Add(e);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        publish();
        await reading;
        return received;
    }

    [Fact]
    public async Task Events_reach_subscribers_on_other_servers_once()
    {
        var bus = new Bus();
        var a = Hub(bus.Join());
        var b = Hub(bus.Join());
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        var onB = await CollectAsync(b, tenant, user, () => a.Publish(new LiveEvent("operation", tenant, user, new { PercentComplete = 50 })));
        var single = Assert.Single(onB);
        Assert.Equal("operation", single.Type);
        Assert.Equal(50, ((JsonElement)single.Data).GetProperty("percentComplete").GetInt32());

        // Local subscribers get the event directly, not a second time from the backplane.
        var onA = await CollectAsync(a, tenant, user, () => a.Publish(new LiveEvent("operation", tenant, null, new { PercentComplete = 60 })));
        Assert.Single(onA);

        // Other tenants and users see nothing.
        Assert.Empty(await CollectAsync(b, Guid.NewGuid(), user, () => a.Publish(new LiveEvent("x", tenant, null, new { }))));
        Assert.Empty(await CollectAsync(b, tenant, Guid.NewGuid(), () => a.Publish(new LiveEvent("x", tenant, user, new { }))));
    }

    [Fact]
    public async Task Large_events_stay_local_and_invalid_messages_are_ignored()
    {
        var bus = new Bus();
        var backplane = bus.Join();
        var a = Hub(backplane);
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        var local = await CollectAsync(a, tenant, user, () => a.Publish(new LiveEvent("big", tenant, user, new { Text = new string('x', 1000) })));
        Assert.Single(local);
        Assert.Equal(0, backplane.Sent);

        Assert.Empty(await CollectAsync(a, tenant, user, () => backplane.Raise("not json")));
    }
}
