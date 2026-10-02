using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace PaperDotNet.Calendar.Features;

internal sealed class CalendarSourceException(string message) : InvalidOperationException(message);

internal sealed record CalendarDownload(string? Text, string? ETag, DateTimeOffset? LastModified);

/// <summary>A bounded, redirect-aware fetcher; secrets never appear in errors.</summary>
internal sealed class CalendarSourceHttp : IDisposable
{
    public const int MaxBytes = 10 * 1024 * 1024;
    private readonly HttpClient _client;

    public CalendarSourceHttp() : this(new HttpClient(CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) }) { }

    internal CalendarSourceHttp(HttpClient client) => _client = client;

    public static bool IsValidUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && value.Length <= 4096;

    public async Task<CalendarDownload> DownloadAsync(string url, string? etag, DateTimeOffset? modified, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ct = timeout.Token;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsValidUrl(url))
            {
                throw new CalendarSourceException("The source must be a public HTTPS iCalendar URL.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // ServiceDefaults excludes credential-bearing requests from URL-bearing HTTP traces.
            request.Options.Set(new HttpRequestOptionsKey<bool>("PaperDotNet.PrivateHttpRequest"), true);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/calendar"));
            if (EntityTagHeaderValue.TryParse(etag, out var validator))
            {
                request.Headers.IfNoneMatch.Add(validator);
            }

            request.Headers.IfModifiedSince = modified;
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ?? throw new CalendarSourceException("The source returned an invalid redirect.");
                url = new Uri(new Uri(url), location).AbsoluteUri;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new CalendarDownload(null, etag, modified);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CalendarSourceException($"The calendar source returned HTTP {(int)response.StatusCode}. Check its URL or try again later.");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new CalendarSourceException("The calendar source exceeds the 10 MiB limit.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > MaxBytes)
                {
                    throw new CalendarSourceException("The calendar source exceeds the 10 MiB limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }

            return new CalendarDownload(new UTF8Encoding(false, true).GetString(output.ToArray()),
                response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        }

        throw new CalendarSourceException("The calendar source returned too many redirects.");
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false, // Resolve and validate the destination ourselves, including each redirected host.
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = addresses.Where(IsPublic).ToArray();
            if (allowed.Length == 0)
            {
                throw new HttpRequestException("The calendar source must resolve to a public address.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Only global unicast 2000::/3; exclude transition/documentation ranges.
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
                && !(bytes[0] == 0x20 && bytes[1] == 0x02) && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0);
        }

        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            && !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && (b[1] == 168 || b[1] == 0 || (b[1] == 2)))
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100)))
                || (b[0] == 203 && b[1] == 0 && b[2] == 113));
    }

    public void Dispose() => _client.Dispose();
}
