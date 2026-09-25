using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PaperDotNet.Api;
using PaperDotNet.Tenancy;

namespace PaperDotNet.Host;

/// <summary>One request of a batch. <c>url</c> is relative to <c>/v1.0</c> (or starts with it).</summary>
public sealed record BatchRequestItem(
    string? Id, string? Method, string? Url, IReadOnlyDictionary<string, string>? Headers, JsonElement? Body, IReadOnlyList<string>? DependsOn);

public sealed record BatchRequest(IReadOnlyList<BatchRequestItem>? Requests);

public sealed record BatchResponseItem(
    string Id, int Status, IReadOnlyDictionary<string, string> Headers, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Body);

public sealed record BatchResponse(IReadOnlyList<BatchResponseItem> Responses);

/// <summary>
/// JSON batching (API-04): <c>POST /v1.0/$batch</c> runs up to 20 API requests in one round trip.
/// Each runs like a normal request (own scope, authentication with the caller's headers, tenant,
/// scopes and permissions), in order; <c>dependsOn</c> skips a request with 424 when a request it
/// depends on failed. Requests are independent: there is no transaction across them.
/// </summary>
internal static class Batch
{
    public const int MaxRequests = 20;
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>Headers of the batch request that sub-requests inherit (authentication and tenant).</summary>
    private static readonly HashSet<string> Inherited = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Host", "Accept-Language",
    };

    /// <summary>
    /// The pipeline for sub-requests: the same routing, tenant resolution, authentication and
    /// authorization as the application, without the outer error and rate-limit handling.
    /// </summary>
    public static RequestDelegate BuildPipeline(WebApplication app)
    {
        var builder = ((IApplicationBuilder)app).New();
        builder.UseRouting();
        builder.UsePaperDotNetTenantResolution();
        builder.UseAuthentication();
        builder.UsePaperDotNetTenantGuard();
        builder.UseAuthorization();
        // A branched pipeline has its own (empty) routing world: route to the application's endpoints.
        builder.UseEndpoints(endpoints =>
        {
            foreach (var source in ((IEndpointRouteBuilder)app).DataSources)
            {
                endpoints.DataSources.Add(source);
            }
        });
        return builder.Build();
    }

    public static void Map(WebApplication app)
    {
        var pipeline = BuildPipeline(app);
        app.MapPost($"{ApiRoutes.V1}/$batch", (BatchRequest request, HttpContext http, CancellationToken ct) => RunAsync(request, http, pipeline, ct))
            .WithTags("Batch")
            .WithName("Batch")
            .RequireAuthorization();
    }

    private static async Task<Results<Ok<BatchResponse>, ValidationProblem>> RunAsync(BatchRequest request, HttpContext outer, RequestDelegate pipeline, CancellationToken ct)
    {
        var requests = request.Requests ?? [];
        var errors = Validate(requests);
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var responses = new List<BatchResponseItem>();
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in requests)
        {
            if ((item.DependsOn ?? []).Any(failed.Contains))
            {
                failed.Add(item.Id!);
                responses.Add(Error(item.Id!, StatusCodes.Status424FailedDependency, "failedDependency", "A request this one depends on failed."));
                continue;
            }

            var response = await SendAsync(item, outer, pipeline, ct);
            if (response.Status >= 400)
            {
                failed.Add(item.Id!);
            }

            responses.Add(response);
        }

        return TypedResults.Ok(new BatchResponse(responses));
    }

    private static Dictionary<string, string[]> Validate(IReadOnlyList<BatchRequestItem> requests)
    {
        var errors = new Dictionary<string, string[]>();
        if (requests.Count is 0 or > MaxRequests)
        {
            errors["requests"] = [$"Send 1 to {MaxRequests} requests."];
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < requests.Count; i++)
        {
            var item = requests[i];
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(item.Id) || !seen.Add(item.Id))
            {
                problems.Add("Each request needs a unique id.");
            }

            if (item.Method is null || !Methods.Contains(item.Method.ToUpperInvariant()))
            {
                problems.Add("The method must be GET, POST, PUT, PATCH or DELETE.");
            }

            if (Target(item.Url) is null)
            {
                problems.Add("The url must be a path of this API (relative to /v1.0), not another batch.");
            }

            if ((item.DependsOn ?? []).Any(d => d == item.Id || !requests.Take(i).Any(r => r.Id == d)))
            {
                problems.Add("dependsOn may only name requests that come earlier in the batch.");
            }

            if (problems.Count > 0)
            {
                errors[$"requests[{i}]"] = [.. problems];
            }
        }

        return errors;
    }

    /// <summary>The path and query of a sub-request, or null when it is not a path of this API.</summary>
    private static (PathString Path, QueryString Query)? Target(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains("://", StringComparison.Ordinal) || url.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var value = url.StartsWith('/') ? url : "/" + url;
        if (!value.StartsWith(ApiRoutes.V1 + "/", StringComparison.OrdinalIgnoreCase))
        {
            value = ApiRoutes.V1 + value;
        }

        var queryStart = value.IndexOf('?', StringComparison.Ordinal);
        var path = new PathString(queryStart < 0 ? value : value[..queryStart]);
        if (path.Value!.Contains("/$batch", StringComparison.OrdinalIgnoreCase) || path.Value.Contains("/../", StringComparison.Ordinal))
        {
            return null;
        }

        return (path, queryStart < 0 ? QueryString.Empty : new QueryString(value[queryStart..]));
    }

    private static async Task<BatchResponseItem> SendAsync(BatchRequestItem item, HttpContext outer, RequestDelegate pipeline, CancellationToken ct)
    {
        var (path, query) = Target(item.Url)!.Value;
        IHeaderDictionary headers = new HeaderDictionary();
        var tenantHeader = outer.RequestServices.GetRequiredService<IOptions<TenancyOptions>>().Value.HeaderName;
        foreach (var header in outer.Request.Headers.Where(h => Inherited.Contains(h.Key) || string.Equals(h.Key, tenantHeader, StringComparison.OrdinalIgnoreCase)))
        {
            headers[header.Key] = header.Value;
        }

        foreach (var (name, value) in item.Headers ?? new Dictionary<string, string>())
        {
            headers[name] = value;
        }

        var body = Stream.Null;
        if (item.Body is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } json)
        {
            var bytes = json.ValueKind == JsonValueKind.String && headers.ContentType.ToString() is { Length: > 0 } type && !type.Contains("json", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetBytes(json.GetString()!)
                : Encoding.UTF8.GetBytes(json.GetRawText());
            body = new MemoryStream(bytes);
            headers.ContentLength = bytes.Length;
            if (StringValues.IsNullOrEmpty(headers.ContentType))
            {
                headers.ContentType = "application/json";
            }
        }

        var responseBody = new MemoryStream();
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Method = item.Method!.ToUpperInvariant(),
            Scheme = outer.Request.Scheme,
            Protocol = outer.Request.Protocol,
            PathBase = outer.Request.PathBase,
            Path = path.Value!,
            QueryString = query.Value ?? "",
            Headers = headers,
            Body = body,
        });
        features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(body != Stream.Null));
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));
        features.Set(outer.Features.Get<IHttpConnectionFeature>());

        var accessor = outer.RequestServices.GetService<IHttpContextAccessor>();
        await using var scope = outer.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var context = new DefaultHttpContext(features) { RequestServices = scope.ServiceProvider, RequestAborted = ct };
        try
        {
            if (accessor is not null)
            {
                accessor.HttpContext = context;
            }

            await pipeline(context);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Error(item.Id!, StatusCodes.Status500InternalServerError, "internalError", "The request failed.");
        }
        finally
        {
            if (accessor is not null)
            {
                accessor.HttpContext = outer;
            }
        }

        return new BatchResponseItem(item.Id!, context.Response.StatusCode, ResponseHeaders(context.Response), ResponseBody(context.Response, responseBody));
    }

    private static Dictionary<string, string> ResponseHeaders(HttpResponse response) =>
        response.Headers.Where(h => h.Key is "Content-Type" or "ETag" or "Location" or "Retry-After")
            .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>JSON bodies stay JSON; text becomes a string; anything else a base64 string.</summary>
    private static JsonElement ResponseBody(HttpResponse response, MemoryStream body)
    {
        if (body.Length == 0)
        {
            return default;
        }

        var type = response.ContentType ?? "";
        var bytes = body.ToArray();
        if (type.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return JsonDocument.Parse(bytes).RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not valid JSON after all: return it as text.
            }
        }

        var text = type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(bytes)
            : Convert.ToBase64String(bytes);
        return JsonSerializer.SerializeToElement(text);
    }

    private static BatchResponseItem Error(string id, int status, string code, string message) =>
        new(id, status, new Dictionary<string, string> { ["Content-Type"] = "application/problem+json" },
            JsonSerializer.SerializeToElement(new { status, code, detail = message }));

    private sealed record BodyDetection(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;
}
