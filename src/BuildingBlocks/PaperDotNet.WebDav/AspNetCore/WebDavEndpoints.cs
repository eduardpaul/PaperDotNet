using System.Xml;
using System.Xml.Serialization;
using FubarDev.WebDavServer;
using FubarDev.WebDavServer.AspNetCore;
using FubarDev.WebDavServer.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PaperDotNet.WebDav;

/// <summary>
/// The WebDAV endpoint (ADR-0047): one Minimal API route for every WebDAV method, calling the vendored dispatcher.
/// Replaces upstream's MVC <c>WebDavControllerBase</c>, its XML input formatter and its exception filter.
/// </summary>
public static partial class WebDavEndpoints
{
    /// <summary>Largest XML request body (PROPFIND, PROPPATCH, LOCK) that is read.</summary>
    public const int MaxXmlBodyBytes = 1024 * 1024;

    public static readonly string[] Methods =
        ["OPTIONS", "GET", "HEAD", "PUT", "DELETE", "PROPFIND", "PROPPATCH", "MKCOL", "COPY", "MOVE", "LOCK", "UNLOCK"];

    /// <summary>Maps <paramref name="prefix"/> and everything below it (route value <c>path</c>) to the WebDAV server.</summary>
    public static IEndpointConventionBuilder MapWebDav(this IEndpointRouteBuilder endpoints, string prefix) =>
        endpoints.MapMethods(prefix.TrimEnd('/') + "/{**path}", Methods, HandleAsync).ExcludeFromDescription();

    private static async Task HandleAsync(HttpContext http)
    {
        var services = http.RequestServices;
        IWebDavResult result;
        try
        {
            result = await DispatchAsync(http, services.GetRequiredService<IWebDavDispatcher>(), http.RequestAborted);
        }
        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
        {
            http.Response.StatusCode = StatusCodes.Status501NotImplemented;
            return;
        }
        catch (WebDavException ex)
        {
            result = ErrorResult(http, ex.StatusCode, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            result = ErrorResult(http, WebDavStatusCode.Forbidden, ex.Message);
        }

        await ExecuteAsync(http, result);
    }

    private static async Task<IWebDavResult> DispatchAsync(HttpContext http, IWebDavDispatcher dispatcher, CancellationToken ct)
    {
        var request = http.Request;
        var path = http.GetRouteValue("path")?.ToString() ?? string.Empty;
        switch (request.Method.ToUpperInvariant())
        {
            case "OPTIONS":
                return await dispatcher.Class1.OptionsAsync(path, ct);
            case "GET":
                return await dispatcher.Class1.GetAsync(path, ct);
            case "HEAD":
                return await dispatcher.Class1.HeadAsync(path, ct);
            case "PUT":
                return await dispatcher.Class1.PutAsync(path, request.Body, ct);
            case "DELETE":
                return await dispatcher.Class1.DeleteAsync(path, ct);
            case "MKCOL":
                if (request.ContentLength > 0 || !string.IsNullOrEmpty(request.ContentType))
                {
                    // MKCOL bodies are not supported (RFC 4918 9.3; litmus basic 14).
                    throw new WebDavException(WebDavStatusCode.UnsupportedMediaType);
                }

                return await dispatcher.Class1.MkColAsync(path, ct);
            case "PROPFIND":
                return await dispatcher.Class1.PropFindAsync(path, await ReadXmlAsync<propfind>(http, ct), ct);
            case "PROPPATCH":
                var update = await ReadXmlAsync<propertyupdate>(http, ct)
                    ?? throw new WebDavException(WebDavStatusCode.BadRequest, "A PROPPATCH needs a propertyupdate body.");
                return await dispatcher.Class1.PropPatchAsync(path, update, ct);
            case "COPY":
                return await dispatcher.Class1.CopyAsync(path, Destination(request), ct);
            case "MOVE":
                return await dispatcher.Class1.MoveAsync(path, Destination(request), ct);
            case "LOCK":
                return await LockAsync(http, dispatcher, path, ct);
            case "UNLOCK":
                if (dispatcher.Class2 is null)
                {
                    return new WebDavResult(WebDavStatusCode.MethodNotAllowed);
                }

                var lockToken = request.Headers["Lock-Token"].ToString();
                if (lockToken.Length == 0)
                {
                    return new WebDavResult(WebDavStatusCode.BadRequest);
                }

                return await dispatcher.Class2.UnlockAsync(path, LockTokenHeader.Parse(lockToken), ct);
            default:
                return new WebDavResult(WebDavStatusCode.MethodNotAllowed);
        }
    }

    private static async Task<IWebDavResult> LockAsync(HttpContext http, IWebDavDispatcher dispatcher, string path, CancellationToken ct)
    {
        if (dispatcher.Class2 is null)
        {
            return new WebDavResult(WebDavStatusCode.MethodNotAllowed);
        }

        var info = await ReadXmlAsync<lockinfo>(http, ct);
        if (info is not null)
        {
            return await dispatcher.Class2.LockAsync(path, info, ct);
        }

        // A LOCK without a body refreshes the lock named in the If header.
        var context = http.RequestServices.GetRequiredService<IWebDavContext>();
        if (context.RequestHeaders.If is not { Count: 1 } ifHeaders)
        {
            return new WebDavResult(WebDavStatusCode.BadRequest);
        }

        return await dispatcher.Class2.RefreshLockAsync(path, ifHeaders[0], context.RequestHeaders.Timeout, ct);
    }

    private static Uri Destination(HttpRequest request)
    {
        var destination = request.Headers["Destination"].ToString();
        return destination.Length != 0 && Uri.TryCreate(destination, UriKind.RelativeOrAbsolute, out var uri)
            ? uri
            : throw new WebDavException(WebDavStatusCode.BadRequest, "A Destination header is required.");
    }

    /// <summary>
    /// Reads an XML request body; null when there is none. Like upstream, a body without a content type is accepted
    /// (litmus). DTDs are refused and the size is limited.
    /// </summary>
    private static async Task<T?> ReadXmlAsync<T>(HttpContext http, CancellationToken ct)
        where T : class
    {
        var request = http.Request;
        if (request.ContentLength is 0)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxXmlBodyBytes)
            {
                throw new WebDavException((WebDavStatusCode)StatusCodes.Status413PayloadTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        buffer.Position = 0;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(buffer, settings);
            return (T?)Serializer<T>.Instance.Deserialize(reader);
        }
        catch (InvalidOperationException ex)
        {
            throw new WebDavException(WebDavStatusCode.BadRequest, ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static IWebDavResult ErrorResult(HttpContext http, WebDavStatusCode statusCode, string message)
    {
        if (statusCode == WebDavStatusCode.NotModified)
        {
            return new WebDavResult(statusCode);
        }

        return new WebDavResult<multistatus>(
            statusCode,
            new multistatus
            {
                response =
                [
                    new response
                    {
                        href = http.Request.GetEncodedUrl(),
                        ItemsElementName = [ItemsChoiceType2.status],
                        Items = [new Status(http.Request.Protocol, statusCode, message).ToString()],
                    },
                ],
            });
    }

    private static async Task ExecuteAsync(HttpContext http, IWebDavResult result)
    {
        var response = http.Response;
        if (result is IDisposable disposable)
        {
            response.RegisterForDispose(disposable);
        }

        response.StatusCode = (int)result.StatusCode;
        if (http.Features.Get<IHttpResponseFeature>() is { } feature)
        {
            feature.ReasonPhrase = result.StatusCode.GetReasonPhrase();
        }

        var context = http.RequestServices.GetRequiredService<IWebDavContext>();
        try
        {
            await result.ExecuteResultAsync(new WebDavResponse(context, response), http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !response.HasStarted)
        {
            LogWriteFailed(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebDavEndpoints)), ex, http.Request.Method);
            response.Clear();
            response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "WebDAV {Method} failed while writing the response")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, string method);

    private static class Serializer<T>
    {
        public static readonly XmlSerializer Instance = new(typeof(T));
    }
}
