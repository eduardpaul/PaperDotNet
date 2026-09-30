using System.Collections.Concurrent;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Test mutator: only acts on lists named "Hooked".</summary>
internal sealed class TestMutator : IItemMutator
{
    public int Sequence => 10;

    public bool AppliesTo(ItemEventScope scope) => scope.ListName == "Hooked";

    public ValueTask ItemAddingAsync(ItemMutationContext context, CancellationToken cancellationToken)
    {
        var title = context.After!["title"]!.GetValue<string>();
        if (title == "forbidden")
        {
            context.Cancel("Titles cannot be 'forbidden'.");
        }
        else if (title == "invalid-by-mutator")
        {
            context.After["amount"] = "not a number";
        }
        else if (context.After["code"] is { } code)
        {
            context.After["code"] = code.GetValue<string>().ToUpperInvariant();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ItemUpdatingAsync(ItemMutationContext context, CancellationToken cancellationToken)
    {
        if (context.Before!["status"]?.GetValue<string>() == "paid")
        {
            context.Cancel("Paid items are locked.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ItemDeletingAsync(ItemMutationContext context, CancellationToken cancellationToken)
    {
        if (context.Before!["title"]!.GetValue<string>().StartsWith("keep", StringComparison.Ordinal))
        {
            context.Cancel("This item must be kept.");
        }

        return ValueTask.CompletedTask;
    }
}

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

/// <summary>Always fails for items in <see cref="FailingLists"/>; other subscribers must still get those events, once.</summary>
internal sealed class FailingSubscriber : IEventSubscriber<ItemAdded>
{
    public static readonly ConcurrentDictionary<Guid, bool> FailingLists = new();

    public static readonly ConcurrentDictionary<Guid, int> Attempts = new();

    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken)
    {
        if (!FailingLists.ContainsKey(integrationEvent.ListId))
        {
            return Task.CompletedTask;
        }

        Attempts.AddOrUpdate(integrationEvent.ItemId, 1, (_, count) => count + 1);
        throw new InvalidOperationException("This subscriber is broken.");
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

/// <summary>Records webhook requests; hosts starting with <c>fail.</c> answer 500; validation requests get their token back.</summary>
internal sealed class TestWebhookReceiver : HttpMessageHandler
{
    public static readonly TestWebhookReceiver Instance = new();

    public System.Collections.Concurrent.ConcurrentQueue<(Uri Url, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue((request.RequestUri!, headers, body));
        var validationToken = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["validationToken"];
        if (validationToken is not null && !request.RequestUri.Host.StartsWith("fail.", StringComparison.Ordinal))
        {
            // Change subscription handshake: echo the token (API-06).
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(validationToken) };
        }

        return new HttpResponseMessage(request.RequestUri!.Host.StartsWith("fail.", StringComparison.Ordinal)
            ? System.Net.HttpStatusCode.InternalServerError
            : System.Net.HttpStatusCode.OK);
    }
}

/// <summary>
/// A template section of the sample extension (PRV-05) in <c>urn:test:marker</c>: records the values it
/// applies per tenant and exports the last one. Registered gated like an extension's section.
/// </summary>
internal sealed class TestTemplateHandler(PaperDotNet.Abstractions.ITenantContext tenant) : PaperDotNet.Provisioning.Contracts.ITemplateHandler
{
    public static readonly System.Xml.Linq.XNamespace Ns = "urn:test:marker";

    public static System.Collections.Concurrent.ConcurrentDictionary<Guid, string> Applied { get; } = new();

    public System.Xml.Linq.XName Element => Ns + "Marker";

    public PaperDotNet.Provisioning.Contracts.TemplateLevel Level => PaperDotNet.Provisioning.Contracts.TemplateLevel.Tenant;

    public int Order => 1000;

    public Task<System.Xml.Linq.XElement?> ExportAsync(PaperDotNet.Provisioning.Contracts.TemplateContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Applied.TryGetValue(tenant.TenantId!.Value, out var value)
            ? new System.Xml.Linq.XElement(Element, new System.Xml.Linq.XAttribute("Value", value))
            : null);

    public Task ApplyAsync(System.Xml.Linq.XElement section, PaperDotNet.Provisioning.Contracts.TemplateContext context, CancellationToken cancellationToken)
    {
        if (!context.DryRun)
        {
            Applied[tenant.TenantId!.Value] = (string?)section.Attribute("Value") ?? string.Empty;
        }

        return Task.CompletedTask;
    }
}
