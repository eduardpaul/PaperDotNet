using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;

namespace PaperDotNet.Jobs.Features;

/// <summary>
/// Server-sent events for the caller (API-07): <c>operation</c> status and progress, and other live notifications.
/// Clients reconnect when the stream ends. Event data is JSON text (no reflection, ADR-0039).
/// </summary>
internal static class LiveEventsEndpoint
{
    /// <summary>How long the caller's principals are reused before an event with an audience loads them again.</summary>
    private static readonly TimeSpan PrincipalsRefresh = TimeSpan.FromMinutes(1);

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/v1.0/me/events", Stream).RequireAuthorization().WithTags("Me").WithName("StreamEvents")
            .WithDescription("Server-sent events for the caller: operation progress and other live notifications.");

    private static ServerSentEventsResult<string> Stream(Caller caller, ILiveEvents live, IServiceProvider services, TimeProvider time, CancellationToken cancellationToken) =>
        TypedResults.ServerSentEvents(Events(live, services.GetService<IPrincipalSet>(), time, caller.TenantId, caller.UserId, cancellationToken));

    private static async IAsyncEnumerable<SseItem<string>> Events(
        ILiveEvents live, IPrincipalSet? principalSet, TimeProvider time, Guid tenantId, Guid userId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Subscribe before the first event, which tells the client the stream is live: nothing after it is missed.
        var events = live.SubscribeAsync(tenantId, userId, cancellationToken);
        yield return new SseItem<string>($$"""{"tenantId":"{{tenantId}}","userId":"{{userId}}"}""", "connected");
        HashSet<Guid>? principals = null;
        var loadedAt = DateTimeOffset.MinValue;
        await foreach (var liveEvent in events)
        {
            // Events with an audience (e.g. item changes, ADR-0035) only reach users with one of its principals.
            if (liveEvent.Audience is { } audience)
            {
                if (principals is null || time.GetUtcNow() - loadedAt > PrincipalsRefresh)
                {
                    principals = principalSet is null ? [userId] : [.. await principalSet.GetPrincipalsAsync(tenantId, userId, cancellationToken)];
                    loadedAt = time.GetUtcNow();
                }

                if (!audience.Any(principals.Contains))
                {
                    continue;
                }
            }

            yield return new SseItem<string>(liveEvent.Data.ToJsonString(), liveEvent.Type);
        }
    }
}
