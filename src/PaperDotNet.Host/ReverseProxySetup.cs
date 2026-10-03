using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace PaperDotNet.Host;

/// <summary>
/// Running behind a reverse proxy (ADR-0045): forwarded headers only from known proxies, server-sent events that pass
/// through buffering proxies, and warnings for setups that trust more than they should.
/// </summary>
internal static class ReverseProxySetup
{
    /// <summary>
    /// The proxies whose forwarded headers count: <c>ForwardedHeaders:KnownProxies</c>, else the sign-in proxies
    /// (<c>Auth:ReverseProxy:TrustedProxies</c>); empty means every peer, as before ADR-0045.
    /// </summary>
    public static IReadOnlyList<string> KnownProxies(IConfiguration configuration) =>
        configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() is { Length: > 0 } known
            ? known
            : configuration.GetSection("Auth:ReverseProxy:TrustedProxies").Get<string[]>() ?? [];

    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = configuration.GetValue("ForwardedHeaders:ForwardLimit", 1);
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var entry in KnownProxies(configuration))
        {
            if (System.Net.IPNetwork.TryParse(entry, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownProxies (or Auth:ReverseProxy:TrustedProxies) has '{entry}', which is neither an address nor a CIDR network.");
            }
        }
    }

    /// <summary>
    /// Tells nginx-based proxies (Nginx Proxy Manager) not to buffer event streams, so live events and MCP's streamed
    /// responses arrive at once. Only requests that accept event streams pay for the callback.
    /// </summary>
    public static void UseEventStreamsThroughProxies(WebApplication app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.OnStarting(static state =>
                {
                    var response = (HttpResponse)state;
                    if (response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        response.Headers["X-Accel-Buffering"] = "no";
                    }

                    return Task.CompletedTask;
                }, context.Response);
            }

            return next(context);
        });
}

/// <summary>Logs proxy settings that trust more than they should (ADR-0045), once at startup.</summary>
internal sealed partial class ReverseProxyWarnings(IConfiguration configuration, ILogger<ReverseProxyWarnings> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool>("ForwardedHeaders:Enabled") && ReverseProxySetup.KnownProxies(configuration).Count == 0)
        {
            LogForwardedHeadersFromAnyone();
        }

        if (configuration.GetValue<bool>("Auth:ReverseProxy:Enabled") && string.IsNullOrEmpty(configuration["Auth:ReverseProxy:Secret"]))
        {
            LogProxySignInWithoutSecret();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "ForwardedHeaders:Enabled trusts forwarded headers from every client. Set ForwardedHeaders:KnownProxies to the proxy's address, "
        + "or do not let clients reach the server directly.")]
    private partial void LogForwardedHeadersFromAnyone();

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Auth:ReverseProxy is enabled without a Secret: any request through the proxy that reaches a sign-in path without being "
        + "authenticated there can name a user. Set Auth:ReverseProxy:Secret and send it from the proxy (ADR-0045).")]
    private partial void LogProxySignInWithoutSecret();
}
