using Microsoft.AspNetCore.Http;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;

namespace PaperDotNet.Tenancy.Resolution;

/// <summary>
/// Runs after authentication. API requests need a resolved tenant, and an
/// authenticated caller may only act in the tenant its token was issued for.
/// </summary>
internal sealed class TenantGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenant)
    {
        if (!context.Request.Path.StartsWithSegments(ApiRoutes.V1))
        {
            await next(context);
            return;
        }

        if (tenant.TenantId is not { } tenantId)
        {
            await ApiErrors.Problem(StatusCodes.Status404NotFound, "tenantNotFound", "No active tenant matches this request.")
                .ExecuteAsync(context);
            return;
        }

        var claimed = context.User.FindFirst(PaperDotNetClaims.TenantId)?.Value;
        if (context.User.Identity?.IsAuthenticated == true && claimed != tenantId.ToString())
        {
            await ApiErrors.Problem(StatusCodes.Status403Forbidden, "tenantMismatch", "The credentials were issued for a different tenant.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
