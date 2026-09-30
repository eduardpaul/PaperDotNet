using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

public sealed class JobsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid _key = Guid.NewGuid();
    private readonly TestHost _host;

    public JobsTests()
    {
        var key = _key;
        _host = new TestHost(
            services =>
            {
                services.AddOperationHandler<SumOperation>();
                services.AddTenantRecurringJob<CountingJob>($"test.counting.{key:N}", "* * * * * *");
                services.AddSingleton(new CountingJob.Key(key));
            },
            new Dictionary<string, string> { ["Jobs:SchedulerInterval"] = "00:00:00.200" });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<(HttpClient Client, ChangeActor Actor)> SignInAsync(string tenant = "default")
    {
        var client = tenant == "default" ? await _host.SignInAsync() : await _host.CreateTenantAsync(tenant);
        var me = await (await client.GetAsync("/v1.0/me", Ct)).JsonAsync(HttpStatusCode.OK);
        return (client, new ChangeActor(Guid.Parse(me.GetProperty("tenantId").GetString()!), Guid.Parse(me.Id())));
    }

    private async Task<Guid> StartAsync(ChangeActor actor, SumPayload payload)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOperations>().StartAsync(actor, SumOperation.Name, payload, TestJson.Default.SumPayload, Ct);
    }

    private static async Task<JsonElement> FinishedAsync(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var operation = await (await client.GetAsync($"/v1.0/operations/{id}", Ct)).JsonAsync(HttpStatusCode.OK);
            if (operation.GetProperty("status").GetString() is OperationStatus.Succeeded or OperationStatus.Failed)
            {
                return operation;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"The operation {id} did not finish.");
    }

    [Fact]
    public async Task Operations_run_in_the_background_for_the_user_who_started_them()
    {
        var (client, actor) = await SignInAsync();

        var sum = await FinishedAsync(client, await StartAsync(actor, new SumPayload([1, 2, 3])));
        var failed = await FinishedAsync(client, await StartAsync(actor, new SumPayload([-1])));

        Assert.Equal(OperationStatus.Succeeded, sum.GetProperty("status").GetString());
        Assert.Equal(100, sum.GetProperty("percentComplete").GetInt32());
        Assert.Equal(6, sum.GetProperty("result").GetProperty("sum").GetInt32());
        Assert.Equal(actor.UserId.ToString(), sum.GetProperty("result").GetProperty("startedBy").GetString());
        Assert.Equal(OperationStatus.Failed, failed.GetProperty("status").GetString());
        Assert.Equal("Negative numbers are not allowed.", failed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Operations_of_another_tenant_or_user_are_not_found()
    {
        var (client, actor) = await SignInAsync();
        var id = await StartAsync(actor, new SumPayload([1]));
        await FinishedAsync(client, id);
        var (other, _) = await SignInAsync("jobs-other");

        using var response = await other.GetAsync($"/v1.0/operations/{id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Live_events_stream_operation_progress_to_the_user()
    {
        var (client, actor) = await SignInAsync();
        using var response = await client.GetAsync("/v1.0/me/events", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        Assert.Equal("event: connected", await reader.ReadLineAsync(Ct));

        var id = await StartAsync(actor, new SumPayload([4, 5]));

        var statuses = new List<string>();
        while (!statuses.Contains(OperationStatus.Succeeded))
        {
            var line = await reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Ct);
            if (line is not null && line.StartsWith("data: ", StringComparison.Ordinal) && JsonNode.Parse(line[6..]) is JsonObject { } data
                && data["id"]?.GetValue<Guid>() == id)
            {
                statuses.Add(data["status"]!.GetValue<string>());
            }
        }

        Assert.Equal(OperationStatus.Running, statuses[0]);
    }

    [Fact]
    public async Task Recurring_jobs_run_for_every_active_tenant()
    {
        var (_, first) = await SignInAsync();
        var (_, second) = await SignInAsync("jobs-second");
        var (_, suspended) = await SignInAsync("jobs-suspended");
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().SetStatusAsync("jobs-suspended", TenantStatus.Suspended, Ct);
        }

        var since = DateTimeOffset.UtcNow;
        for (var attempt = 0; attempt < 150 && !(RanSince(first.TenantId, since) && RanSince(second.TenantId, since)); attempt++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(RanSince(first.TenantId, since));
        Assert.True(RanSince(second.TenantId, since));
        Assert.False(RanSince(suspended.TenantId, since.AddSeconds(1)));
    }

    private bool RanSince(Guid tenantId, DateTimeOffset since) =>
        CountingJob.Runs.TryGetValue((_key, tenantId), out var runs) && runs.Any(r => r > since);

    internal sealed record SumPayload(int[] Numbers);

    private sealed class SumOperation : OperationHandler<SumPayload>
    {
        public const string Name = "test.sum";

        public override string Type => Name;

        protected override JsonTypeInfo<SumPayload> PayloadJson => TestJson.Default.SumPayload;

        protected override async Task<JsonNode?> ExecuteAsync(SumPayload payload, OperationContext context, CancellationToken cancellationToken)
        {
            if (payload.Numbers.Any(n => n < 0))
            {
                throw new InvalidOperationException("Negative numbers are not allowed.");
            }

            await context.Progress.ReportAsync(50, cancellationToken);
            return new JsonObject { ["sum"] = payload.Numbers.Sum(), ["startedBy"] = context.Actor.UserId };
        }
    }

    private sealed class CountingJob(CountingJob.Key key) : ITenantRecurringJob
    {
        public static readonly ConcurrentDictionary<(Guid Key, Guid Tenant), ConcurrentBag<DateTimeOffset>> Runs = new();

        public Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            Runs.GetOrAdd((key.Value, tenantId), _ => []).Add(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }

        internal sealed record Key(Guid Value);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JobsTests.SumPayload))]
internal sealed partial class TestJson : JsonSerializerContext;
