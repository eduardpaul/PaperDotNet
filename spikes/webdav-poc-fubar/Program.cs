using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FubarDev.WebDavServer;
using FubarDev.WebDavServer.AspNetCore;
using FubarDev.WebDavServer.AspNetCore.Filters;
using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Locking;
using FubarDev.WebDavServer.Locking.InMemory;
using FubarDev.WebDavServer.Props.Store;
using FubarDev.WebDavServer.Props.Store.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WebDavPoc;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Poc").Get<PocSettings>() ?? new PocSettings();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<PocModel>();
builder.Services.AddHttpContextAccessor();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("FubarDev", builder.Configuration.GetValue("Poc:FubarLogLevel", LogLevel.Warning));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);

builder.Services.AddAuthentication(PocBasicHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PocBasicHandler>(PocBasicHandler.SchemeName, null);

builder.Services.AddHttpContextAccessor();
builder.Services
    .AddMvcCore()
    .AddAuthorization(o => o.AddPolicy("dav", policy => policy.RequireAssertion(ctx =>
        ctx.User.Identity?.IsAuthenticated == true
        || (ctx.Resource as HttpContext ?? (ctx.Resource as Microsoft.AspNetCore.Mvc.Filters.AuthorizationFilterContext)?.HttpContext)
            ?.Request.Method == HttpMethods.Options)))
    .AddWebDav(o => o.EnableClass2 = !settings.ReadOnly);
builder.Services.AddSingleton<IFileSystemFactory, PocFileSystemFactory>();
if (settings.DeadProperties)
{
    builder.Services.AddSingleton<IPropertyStoreFactory, InMemoryPropertyStoreFactory>();
}
builder.Services.Replace(ServiceDescriptor.Singleton<IMimeTypeDetector, PocMimeTypeDetector>());
builder.Services.Configure<DefaultTimeoutPolicyOptions>(o => o.MaxTimeout = TimeSpan.FromHours(1));
if (!settings.ReadOnly)
{
    builder.Services.AddSingleton<ILockManager, InMemoryLockManager>();
}

var app = builder.Build();
app.UseAuthentication();
app.UseRouting();
app.UseAuthorization();

app.MapGet("/v1.0/me", (ClaimsPrincipal user) => Results.Ok(new { user.Identity!.Name })).RequireAuthorization();
app.MapGet("/poc/node", (string path, PocModel model) =>
{
    lock (model.Gate)
    {
        var node = model.Root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = DavNames.Assign(node.Children.Where(c => c.Kind != NodeKind.List && (c.Kind != NodeKind.File || c.Content is not null)))
                .FirstOrDefault(c => c.Name == segment);
            if (match.Node is null)
            {
                return Results.NotFound();
            }

            node = match.Node;
        }

        return Results.Ok(new { node.Id, node.Title, node.Kind, node.FileName, node.Version, node.FileVersions, size = node.Content?.Length, recycleBin = model.RecycleBin.Count });
    }
});
app.MapControllers();
app.Run();

[Route("dav/{*path}")]
[Authorize("dav")] // anonymous OPTIONS, like the other spike's gate (Explorer's first request)
[WebDavAnyExceptionFilter]
public sealed class WebDavController(IWebDavContext context, IWebDavDispatcher dispatcher, ILoggerFactory? loggerFactory = null)
    : WebDavControllerBase(context, dispatcher, loggerFactory);

/// <summary>Explorer probes the server root (the library's sample ships the same controller).</summary>
[Route("")]
public sealed class WinRootCompatController : ControllerBase
{
    [HttpOptions]
    public IActionResult QueryOptions()
    {
        Response.Headers["DAV"] = "1, 2";
        Response.Headers["MS-Author-Via"] = "DAV";
        return Ok();
    }
}

public sealed class PocSettings
{
    public string Path { get; set; } = "/dav";
    public bool ReadOnly { get; set; } = true;
    public string Token { get; set; } = "pdn_poc_0123456789abcdef";
    public int MaxFolderEntries { get; set; } = 5000;

    /// <summary>The library keeps getcontenttype/displayname as dead properties; false runs it without a property store.</summary>
    public bool DeadProperties { get; set; } = true;
}

/// <summary>HTTP Basic with an API token as the password, accepted only under the WebDAV path (as in the other spike).</summary>
public sealed class PocBasicHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PocSettings settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PocBasic";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments(settings.Path))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return Task.FromResult(AuthenticateResult.Fail("Malformed Basic credentials."));
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0 || decoded[(separator + 1)..] != settings.Token)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid token."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "poc-user")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (Request.Path.StartsWithSegments(settings.Path))
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"PaperDotNet\", charset=\"UTF-8\"";
        }

        return Task.CompletedTask;
    }
}
