using FubarDev.WebDavServer;
using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Props.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Dav.Features;
using PaperDotNet.WebDav;

namespace PaperDotNet.Dav;

/// <summary>WebDAV settings (section <c>WebDav</c>).</summary>
public sealed class DavOptions
{
    public const string Section = "WebDav";

    /// <summary>Serve <c>/dav</c>. On by default; it only answers callers with an API token.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Most entries one folder lists. WebDAV clients cannot page, so larger folders are cut off (with a warning in the
    /// log); use subfolders.
    /// </summary>
    public int MaxFolderEntries { get; set; } = 5000;
}

/// <summary>
/// WebDAV for document libraries (API-10, ADR-0047): <c>/dav/{workspace}/{library}/{folder…}/{file}</c>, read-only for
/// now. Clients sign in with HTTP Basic and an API token as the password. Built on the extension SDK; the protocol is
/// the vendored FubarDev.WebDavServer (<c>PaperDotNet.WebDav</c>).
/// </summary>
public sealed class DavModule : IModule
{
    /// <summary>The scopes needed to read through WebDAV (their names; the scopes belong to Lists and Documents).</summary>
    internal static readonly string[] ReadScopes = ["list.read", "document.read"];

    /// <summary>Methods that need credentials. OPTIONS does not (clients ask it before they authenticate).</summary>
    internal static readonly string[] AuthenticatedMethods =
        ["GET", "HEAD", "PROPFIND", "PUT", "DELETE", "PROPPATCH", "MKCOL", "COPY", "MOVE", "LOCK", "UNLOCK"];

    public string Name => "Dav";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DavOptions>().BindConfiguration(DavOptions.Section);
        services.AddSingleton<IMimeTypeDetector, DavMimeTypeDetector>();
        services.AddSingleton<IPropertyStoreFactory, EmptyPropertyStoreFactory>();
        services.AddScoped<IFileSystemFactory, DavFileSystemFactory>();
        services.AddWebDavServer(o => o.EnableClass2 = false);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        DavDiscovery.Map(endpoints);
        if (!endpoints.ServiceProvider.GetRequiredService<IOptions<DavOptions>>().Value.Enabled)
        {
            return;
        }

        var read = new AuthorizationPolicyBuilder(AuthenticationSchemeNames.ApiToken)
            .RequireAuthenticatedUser()
            .AddRequirements(new ActiveUserRequirement())
            .AddRequirements([.. ReadScopes.Select(s => new ScopeRequirement(s))])
            .Build();
        endpoints.MapWebDav(ApiRoutes.Dav, "OPTIONS").AllowAnonymous();
        endpoints.MapWebDav(ApiRoutes.Dav, AuthenticatedMethods).RequireAuthorization(read);

        // Windows' WebDAV client asks the server root before the mapped path; a CORS preflight (Origin) is not ours.
        endpoints.MapMethods("/", ["OPTIONS"], (HttpContext context) =>
            {
                if (context.Request.Headers.Origin.Count > 0)
                {
                    return Results.NoContent();
                }

                context.Response.Headers["DAV"] = "1";
                context.Response.Headers["MS-Author-Via"] = "DAV";
                context.Response.Headers.Allow = "OPTIONS";
                return Results.Ok();
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}
