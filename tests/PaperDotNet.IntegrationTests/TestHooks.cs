using System.Collections.Concurrent;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Test receiver: only acts on lists named "Hooked".</summary>
internal sealed class TestReceiver : IItemEventReceiver
{
    public static readonly ConcurrentBag<(ItemEventKind Kind, Guid ItemId, IReadOnlyCollection<string> Changed)> After = [];

    public int Sequence => 10;

    public bool AppliesTo(ItemEventScope scope) => scope.ListName == "Hooked";

    public ValueTask ItemAddingAsync(ItemChangingContext context, CancellationToken cancellationToken)
    {
        var title = context.After!["title"]!.GetValue<string>();
        if (title == "forbidden")
        {
            context.Cancel("Titles cannot be 'forbidden'.");
        }
        else if (title == "invalid-by-receiver")
        {
            context.After["amount"] = "not a number";
        }
        else if (context.After["code"] is { } code)
        {
            context.After["code"] = code.GetValue<string>().ToUpperInvariant();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ItemUpdatingAsync(ItemChangingContext context, CancellationToken cancellationToken)
    {
        if (context.Before!["status"]?.GetValue<string>() == "paid")
        {
            context.Cancel("Paid items are locked.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ItemDeletingAsync(ItemChangingContext context, CancellationToken cancellationToken)
    {
        if (context.Before!["title"]!.GetValue<string>().StartsWith("keep", StringComparison.Ordinal))
        {
            context.Cancel("This item must be kept.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ItemAddedAsync(ItemChangedContext context, CancellationToken cancellationToken)
    {
        After.Add((context.Kind, context.ItemId, context.ChangedFields));
        return context.After!["title"]!.GetValue<string>() == "boom"
            ? throw new InvalidOperationException("After receivers must not break writes.")
            : ValueTask.CompletedTask;
    }

    public ValueTask ItemUpdatedAsync(ItemChangedContext context, CancellationToken cancellationToken)
    {
        After.Add((context.Kind, context.ItemId, context.ChangedFields));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Collects asynchronous item events delivered through the outbox.</summary>
internal sealed class TestSubscriber(ITenantContext tenant) : IEventSubscriber<ItemAdded>, IEventSubscriber<ItemUpdated>
{
    public static readonly ConcurrentBag<(ItemEvent Event, Guid? ResolvedTenant)> Received = [];

    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken)
    {
        Received.Add((integrationEvent, tenant.TenantId));
        return Task.CompletedTask;
    }

    public Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken)
    {
        Received.Add((integrationEvent, tenant.TenantId));
        return Task.CompletedTask;
    }
}

/// <summary>Recurring test job (every second): counts runs per tenant.</summary>
internal sealed class TestRecurringJob(ITenantContext tenant) : ITenantRecurringJob
{
    public static readonly ConcurrentDictionary<Guid, int> Runs = new();

    public Task RunAsync(CancellationToken cancellationToken)
    {
        Runs.AddOrUpdate(tenant.TenantId!.Value, 1, (_, count) => count + 1);
        return Task.CompletedTask;
    }
}

internal static class Eventually
{
    public static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe, TimeSpan? timeout = null)
        where T : struct
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } result)
            {
                return result;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition was not met in time.");
    }

    public static Task<bool> WaitForAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        WaitForAsync(() => Task.FromResult(condition() ? (bool?)true : null), timeout);
}

/// <summary>Records webhook requests; hosts starting with <c>fail.</c> answer 500.</summary>
internal sealed class TestWebhookReceiver : HttpMessageHandler
{
    public static readonly TestWebhookReceiver Instance = new();

    public System.Collections.Concurrent.ConcurrentQueue<(Uri Url, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue((request.RequestUri!, headers, body));
        return new HttpResponseMessage(request.RequestUri!.Host.StartsWith("fail.", StringComparison.Ordinal)
            ? System.Net.HttpStatusCode.InternalServerError
            : System.Net.HttpStatusCode.OK);
    }
}
