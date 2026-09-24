using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PaperDotNet.Api;

public static class ApiRoutes
{
    public const string V1 = "/v1.0";

    /// <summary>Creates a route group under <c>/v1.0/{prefix}</c> that requires authentication.</summary>
    public static RouteGroupBuilder MapV1Group(this IEndpointRouteBuilder endpoints, string prefix, string tag)
    {
        return endpoints.MapGroup($"{V1}/{prefix}")
            .WithTags(tag)
            .RequireAuthorization();
    }
}
