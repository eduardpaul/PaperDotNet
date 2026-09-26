using Microsoft.Extensions.FileProviders;
using PaperDotNet.Identity.Authentication;

namespace PaperDotNet.Host;

/// <summary>
/// Serves the web UI (ADR-0033) from the same origin as the API: the built app in <c>Web:RootPath</c>
/// (default <c>wwwroot</c>), with <c>index.html</c> for client routes. Without an <c>index.html</c>
/// (API-only builds, development with the Vite dev server) nothing changes.
/// </summary>
internal static class WebUi
{
    /// <summary>Paths that belong to the server; unknown paths below them are 404s, never the app.</summary>
    private static readonly string[] ServerPaths = ["/v1.0", "/connect", "/.well-known", "/health", "/openapi", "/version"];

    // The app loads only its own scripts; Radix positions popovers with inline styles; previews use blob: URLs.
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; " +
        "connect-src 'self'; font-src 'self'; object-src 'none'; frame-src 'self' blob:; frame-ancestors 'none'; " +
        "base-uri 'self'; form-action 'self'";

    /// <summary>The folder with the built UI, or null when this build has none.</summary>
    public static string? RootPath(IConfiguration configuration, IHostEnvironment environment)
    {
        var path = configuration["Web:RootPath"] is { Length: > 0 } configured
            ? Path.GetFullPath(configured, environment.ContentRootPath)
            : Path.Combine(environment.ContentRootPath, "wwwroot");
        return File.Exists(Path.Combine(path, "index.html")) ? path : null;
    }

    public static void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (RootPath(configuration, environment) is null)
        {
            return;
        }

        // /connect/authorize sends signed-out users to the UI's sign-in page unless configured otherwise.
        services.PostConfigure<AuthOptions>(o =>
        {
            if (string.IsNullOrEmpty(o.LoginUrl))
            {
                o.LoginUrl = "/login";
            }
        });
    }

    /// <summary>Static files; before authentication and tenant resolution, since they are public and the same for everyone.</summary>
    public static void UseStaticFiles(WebApplication app)
    {
        if (RootPath(app.Configuration, app.Environment) is not { } root)
        {
            return;
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                // Vite puts content-hashed files in /assets; everything else may change with a new version.
                headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
                headers.XContentTypeOptions = "nosniff";
            },
        });
    }

    /// <summary>index.html for every other GET outside the server's paths, so client routes survive a reload.</summary>
    public static void MapFallback(WebApplication app)
    {
        if (RootPath(app.Configuration, app.Environment) is not { } root)
        {
            return;
        }

        var index = new PhysicalFileProvider(root).GetFileInfo("index.html");
        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                || ServerPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var headers = context.Response.Headers;
            headers.CacheControl = "no-cache";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(index, context.RequestAborted);
        }).AllowAnonymous().ExcludeFromDescription();
    }
}
