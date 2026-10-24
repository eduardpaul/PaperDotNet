using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;

namespace PaperDotNet.Host.Content;

internal static class ContentMigrationMiddleware
{
    public static async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        try
        {
            var parts = http.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (http.User.Identity?.IsAuthenticated == true
                && parts.Length > 2 && parts[0] == "v1.0" && parts[1] == "workspaces" && Guid.TryParse(parts[2], out var workspace))
            {
                var tenant = http.RequestServices.GetRequiredService<ITenantContext>();
                var factory = http.RequestServices.GetRequiredService<ContentDatabaseFactory>();
                if (tenant.TenantId is { } tenantId && await factory.ExistsAsync(tenantId, http.RequestAborted))
                {
                    await using var db = await factory.OpenAsync(tenantId, http.RequestAborted);
                    if (await db.Set<PaperDotNet.Content.AuditEventsRow>().AnyAsync(x => x.Action == "content.migrate" && x.WorkspaceId == workspace.ToString(), http.RequestAborted))
                        throw new ContentMigratedException();
                }
            }
            await next(http);
        }
        catch (ContentMigratedException exception)
        { await ApiErrors.Conflict("contentMigrated", exception.Message).ExecuteAsync(http); }
    }
}
