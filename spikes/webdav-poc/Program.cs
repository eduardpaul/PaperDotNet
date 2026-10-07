using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Dav.AspNetCore.Server;
using Dav.AspNetCore.Server.Store;
using Dav.AspNetCore.Server.Store.Properties;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using WebDavPoc;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Poc").Get<PocSettings>() ?? new PocSettings();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<PocModel>();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddAuthentication(PocBasicHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PocBasicHandler>(PocBasicHandler.SchemeName, null);
builder.Services.AddAuthorization();

PocFile.Register();
PocCollection.Register();
builder.Services.AddWebDav(dav =>
{
    dav.AddStore<PocStoreOptions, PocStore>();
    if (settings.DeadProperties)
    {
        dav.AddPropertyStore<PropertyStoreOptions, InMemoryPropertyStore>();
    }
    else
    {
        dav.AddPropertyStore<PropertyStoreOptions, NoopPropertyStore>();
    }
    dav.RequiresAuthentication = true;
    dav.DisallowInfinityDepth = true;
    dav.MaxLockTimeout = TimeSpan.FromHours(1);
    dav.ServerName = "PaperDotNet WebDAV PoC";
});

var app = builder.Build();
app.UseAuthentication();

// Windows Mini-Redirector may probe the server root before the mapped path (plan: WD-1 spike item).
app.MapMethods("/", ["OPTIONS"], (HttpContext context) =>
{
    if (context.Request.Headers.Origin.Count > 0)
    {
        return Results.StatusCode(StatusCodes.Status204NoContent); // a CORS preflight is not ours
    }

    DavGate.WriteOptions(context, settings);
    return Results.Ok();
});

// Basic must be ignored outside /dav: an API endpoint that requires authentication.
app.MapGet("/v1.0/me", (ClaimsPrincipal user) => Results.Ok(new { user.Identity!.Name })).RequireAuthorization();

// Test hooks (spike only, never in the product).
app.MapGet("/poc/node", (string path, PocModel model) =>
{
    lock (model.Gate)
    {
        var node = Find(model, path);
        return node is null
            ? Results.NotFound()
            : Results.Ok(new { node.Id, node.Title, node.Kind, node.FileName, node.Version, node.FileVersions, size = node.Content?.Length, recycleBin = model.RecycleBin.Count });
    }
});

app.Map(settings.Path, dav =>
{
    dav.UseMiddleware<DavGate>();
    dav.UseWebDav();
});

app.Run();

static Node? Find(PocModel model, string path)
{
    var node = model.Root;
    foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
        var match = DavNames.Assign(node.Children.Where(c => c.Kind != NodeKind.List && (c.Kind != NodeKind.File || c.Content is not null)))
            .FirstOrDefault(c => c.Name == segment);
        if (match.Node is null)
        {
            return null;
        }

        node = match.Node;
    }

    return node;
}

public sealed class PocSettings
{
    public string Path { get; set; } = "/dav";
    public bool ReadOnly { get; set; } = true;
    public string Token { get; set; } = "pdn_poc_0123456789abcdef";
    public int MaxFolderEntries { get; set; } = 5000;
    public bool DeadProperties { get; set; }
}

/// <summary>
/// What the library cannot be configured to do: advertise class 1 only (read-only), send MS-Author-Via, and refuse
/// writes before they reach the store.
/// </summary>
public sealed class DavGate(RequestDelegate next, PocSettings settings)
{
    private static readonly HashSet<string> ReadMethods = new(StringComparer.OrdinalIgnoreCase) { "OPTIONS", "PROPFIND", "GET", "HEAD" };

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            WriteOptions(context, settings);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (settings.ReadOnly && !ReadMethods.Contains(context.Request.Method))
        {
            var auth = await context.AuthenticateAsync();
            context.Response.StatusCode = auth.Succeeded ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
            if (!auth.Succeeded)
            {
                await context.ChallengeAsync();
            }

            return;
        }

        await next(context);
    }

    public static void WriteOptions(HttpContext context, PocSettings settings)
    {
        context.Response.Headers["DAV"] = settings.ReadOnly ? "1" : "1, 2";
        context.Response.Headers["MS-Author-Via"] = "DAV";
        context.Response.Headers["Allow"] = settings.ReadOnly
            ? "OPTIONS, PROPFIND, GET, HEAD"
            : "OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, DELETE, MKCOL, MOVE, COPY, LOCK, UNLOCK";
    }
}

/// <summary>HTTP Basic with an API token as the password, accepted only under the WebDAV path.</summary>
public sealed class PocBasicHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PocSettings settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PocBasic";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.PathBase.StartsWithSegments(settings.Path) && !Request.Path.StartsWithSegments(settings.Path))
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
        var password = separator < 0 ? "" : decoded[(separator + 1)..];
        if (password != settings.Token)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid token."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "poc-user")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (Request.PathBase.StartsWithSegments(settings.Path) || Request.Path.StartsWithSegments(settings.Path))
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"PaperDotNet\", charset=\"UTF-8\"";
        }

        return Task.CompletedTask;
    }
}
