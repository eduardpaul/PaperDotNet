using System.Net;
using Microsoft.AspNetCore.Http;

namespace PaperDotNet.Api;

/// <summary>
/// The address of the direct network peer, captured before forwarded headers can replace
/// <see cref="ConnectionInfo.RemoteIpAddress"/> with a client-supplied value. Trust decisions about
/// proxies (IAM-15) use it.
/// </summary>
public static class PeerAddress
{
    private static readonly object Key = new();

    /// <summary>Remembers the connection's address; the host calls it first in the pipeline.</summary>
    public static void Capture(HttpContext context) => context.Items[Key] = context.Connection.RemoteIpAddress;

    public static IPAddress? Get(HttpContext context) =>
        context.Items.TryGetValue(Key, out var value) ? value as IPAddress : context.Connection.RemoteIpAddress;
}
