using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PaperDotNet.Api;

public static class ApiRoutes
{
    public const string V1 = "/v1.0";

    /// <summary>
    /// WebDAV (ADR-0047). The only path where HTTP Basic credentials (an API token as the password) are accepted:
    /// Basic credentials are ambient like cookies, so the rest of the API never reads them.
    /// </summary>
    public const string Dav = "/dav";

    /// <summary>True for <see cref="Dav"/> and everything below it.</summary>
    public static bool IsDav(HttpRequest request) => request.Path.StartsWithSegments(Dav, StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates a route group under <c>/v1.0/{prefix}</c> that requires authentication.</summary>
    public static RouteGroupBuilder MapV1Group(this IEndpointRouteBuilder endpoints, string prefix, string tag)
    {
        return endpoints.MapGroup($"{V1}/{prefix}")
            .WithTags(tag)
            .RequireAuthorization();
    }
}
