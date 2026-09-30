using System.Text.Json.Serialization.Metadata;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace PaperDotNet.Messaging;

internal sealed class WolverineOutbox(IDbContextOutbox outbox, TimeProvider time) : IOutbox
{
    public async Task SaveChangesAsync(DbContext db, IReadOnlyCollection<IntegrationEvent> events, IReadOnlyCollection<object> messages, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0 && messages.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        outbox.Enroll(db);

        // One request may save several times; by default Wolverine flushes a context only once.
        if (outbox is MessageContext context)
        {
            context.MultiFlushMode = MultiFlushMode.AllowMultiples;
        }

        var now = time.GetUtcNow();
        foreach (var integrationEvent in events)
        {
            await outbox.PublishAsync(integrationEvent.OccurredAt == default ? integrationEvent with { OccurredAt = now } : integrationEvent);
        }

        foreach (var message in messages)
        {
            await outbox.PublishAsync(message);
        }

        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }
}

public static class MessagingExtensions
{
    public static IServiceCollection AddPaperDotNetMessaging(this IServiceCollection services)
    {
        services.AddScoped<IOutbox, WolverineOutbox>();
        return services;
    }

    /// <summary>
    /// Messaging rules (ADR-0008, ADR-0039): durable local queues, the EF Core outbox, one queue per subscriber, retries
    /// then dead-lettering, JSON through source-generated metadata, and handlers loaded from code generated ahead of time
    /// (<c>eng/codegen.sh</c>) so nothing is compiled at run time.
    /// </summary>
    public static WolverineOptions UsePaperDotNetDefaults(this WolverineOptions options, IJsonTypeInfoResolver json, bool generatingCode)
    {
        options.UseEntityFrameworkCoreTransactions();
        options.Policies.UseDurableLocalQueues();

        // Each subscriber of an event gets its own message and queue, retried and dead-lettered on its own.
        options.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
        options.UseSystemTextJsonForSerialization(serializer => serializer.TypeInfoResolverChain.Insert(0, json));
        options.CodeGeneration.TypeLoadMode = generatingCode ? TypeLoadMode.Dynamic : TypeLoadMode.Static;

        // Handlers resolve internal services from the scope (plain GetRequiredService in the generated code, fine under AOT).
        options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

        // Subscribers are classes named *Subscriber with Handle methods.
        options.Discovery.CustomizeHandlerDiscovery(query => query.Includes.WithNameSuffix("Subscriber"));

        options.Policies.OnAnyException()
            .RetryWithCooldown(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2))
            .Then.ScheduleRetry(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5))
            .Then.MoveToErrorQueue();
        return options;
    }
}
