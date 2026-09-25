using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Persistence.PostgreSql;

namespace PaperDotNet.IntegrationTests;

/// <summary>Live events across servers over PostgreSQL LISTEN/NOTIFY (ADR-0026).</summary>
public sealed class LiveEventBackplaneTests(PaperDotNetApiFactory factory)
{
    [Fact]
    public async Task Events_published_on_one_server_reach_clients_on_another()
    {
        Assert.SkipUnless(PaperDotNetApiFactory.Provider == "postgresql", "The backplane is PostgreSQL only.");
        var ct = TestContext.Current.CancellationToken;

        // "Server A" is the test host; "server B" gets its own listening connection and hub.
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await factory.Services.GetRequiredService<PostgreSqlLiveEventBackplane>().Listening.WaitAsync(TimeSpan.FromSeconds(10), ct);
        using var otherBackplane = new PostgreSqlLiveEventBackplane(dataSource, NullLogger<PostgreSqlLiveEventBackplane>.Instance);
        await otherBackplane.StartAsync(ct);
        await otherBackplane.Listening.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var serverB = new LiveEventHub(
            factory.Services.GetRequiredService<IOptions<JsonOptions>>(), NullLogger<LiveEventHub>.Instance, otherBackplane);

        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var first = Task.Run(async () =>
        {
            await foreach (var e in serverB.SubscribeAsync(tenant, user, cts.Token))
            {
                return e;
            }

            return null;
        }, ct);
        await Task.Delay(100, ct);

        factory.Services.GetRequiredService<ILiveEvents>().Publish(new LiveEvent("operation", tenant, user, new { PercentComplete = 42 }));
        var received = await first;

        Assert.NotNull(received);
        Assert.Equal("operation", received.Type);
        Assert.Equal(42, ((JsonElement)received.Data).GetProperty("percentComplete").GetInt32());
        await otherBackplane.StopAsync(ct);
    }
}
