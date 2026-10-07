using FubarDev.WebDavServer;
using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Locking;
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
using PaperDotNet.Dav.Data;
using PaperDotNet.Dav.Features;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Persistence;
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

    /// <summary>
    /// A file created empty (Explorer: an empty PUT or a LOCK, then the content) is filled in place, not as a second
    /// version, when its content follows within this time.
    /// </summary>
    public TimeSpan EmptyFileGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Largest temporary file of a desktop app (Office writes the whole document into one while saving).</summary>
    public long MaxTransientFileSize { get; set; } = 100L * 1024 * 1024;

    /// <summary>Longest lock a client gets; longer or infinite requests are shortened (clients refresh their locks).</summary>
    public TimeSpan MaxLockTimeout { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// WebDAV for document libraries (API-10, ADR-0047): <c>/dav/{workspace}/{library}/{folder…}/{file}</c>. Clients sign in
/// with HTTP Basic and an API token as the password; reading needs <c>list.read</c> and <c>document.read</c>, writing
/// also <c>list.write</c> and <c>document.write</c>. Class 2 (locks) for desktop apps. Built on the extension SDK; the
/// protocol is the vendored FubarDev.WebDavServer (<c>PaperDotNet.WebDav</c>).
/// </summary>
public sealed class DavModule : IModule
{
    /// <summary>The scopes needed to read through WebDAV (their names; the scopes belong to Lists and Documents).</summary>
    internal static readonly string[] ReadScopes = ["list.read", "document.read"];

    /// <summary>The scopes needed to change files and folders through WebDAV, besides the read scopes.</summary>
    internal static readonly string[] WriteScopes = ["list.write", "document.write"];

    /// <summary>Methods that read (OPTIONS needs no credentials: clients ask it before they authenticate).</summary>
    internal static readonly string[] ReadMethods = ["GET", "HEAD", "PROPFIND"];

    /// <summary>Methods that write; a token without the write scopes gets 403, so apps open files read-only.</summary>
    internal static readonly string[] WriteMethods = ["PUT", "DELETE", "PROPPATCH", "MKCOL", "COPY", "MOVE", "LOCK", "UNLOCK"];

    public string Name => "Dav";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DavOptions>().BindConfiguration(DavOptions.Section);
        services.AddModuleDbContext<DavDbContext>(DavDbContext.Schema);
        services.AddSingleton<IMimeTypeDetector, DavMimeTypeDetector>();
        services.AddSingleton<IPropertyStoreFactory, EmptyPropertyStoreFactory>();
        services.AddScoped<IFileSystemFactory, DavFileSystemFactory>();
        services.AddScoped<DavTransientStore>();
        services.AddScoped<ILockManager, DavLockManager>();
        services.AddSingleton<ILockCleanupTask, DavLockCleanup>();
        services.AddOptions<DefaultTimeoutPolicyOptions>().Configure<IOptions<DavOptions>>((policy, dav) =>
        {
            policy.AllowInfiniteTimeout = false;
            policy.MaxTimeout = dav.Value.MaxLockTimeout;
        });
        services.AddWebDavServer(o => o.EnableClass2 = true);
        services.AddTenantRecurringJob<DavCleanupJob>(DavCleanupJob.Name, DavCleanupJob.Schedule);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        DavDiscovery.Map(endpoints);
        if (!endpoints.ServiceProvider.GetRequiredService<IOptions<DavOptions>>().Value.Enabled)
        {
            return;
        }

        static AuthorizationPolicy Policy(IEnumerable<string> scopes) => new AuthorizationPolicyBuilder(AuthenticationSchemeNames.ApiToken)
            .RequireAuthenticatedUser()
            .AddRequirements(new ActiveUserRequirement())
            .AddRequirements([.. scopes.Select(s => new ScopeRequirement(s))])
            .Build();
        endpoints.MapWebDav(ApiRoutes.Dav, "OPTIONS").AllowAnonymous();
        endpoints.MapWebDav(ApiRoutes.Dav, ReadMethods).RequireAuthorization(Policy(ReadScopes));
        endpoints.MapWebDav(ApiRoutes.Dav, WriteMethods).RequireAuthorization(Policy([.. ReadScopes, .. WriteScopes]));

        // Windows' WebDAV client asks the server root before the mapped path; a CORS preflight (Origin) is not ours.
        endpoints.MapMethods("/", ["OPTIONS"], (HttpContext context) =>
            {
                if (context.Request.Headers.Origin.Count > 0)
                {
                    return Results.NoContent();
                }

                context.Response.Headers["DAV"] = "1, 2";
                context.Response.Headers["MS-Author-Via"] = "DAV";
                context.Response.Headers.Allow = "OPTIONS";
                return Results.Ok();
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}
