using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Host;
using PaperDotNet.Identity.Authentication;

namespace PaperDotNet.IntegrationTests;

/// <summary>The host serves the built web UI from the API's origin (ADR-0033).</summary>
public sealed class WebUiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pdn_web_{Guid.NewGuid():N}");
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), "<!doctype html><div id=root></div>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "assets", "app-3f2a.js"), "console.log(1)", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "favicon.svg"), "<svg/>", TestContext.Current.CancellationToken);
        _app = CreateApp(_root);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
    }

    private static WebApplication CreateApp(string root)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Web:RootPath"] = root;
        builder.Services.AddOptions<AuthOptions>();
        WebUi.AddServices(builder.Services, builder.Configuration, builder.Environment);
        var app = builder.Build();
        WebUi.UseStaticFiles(app);
        app.MapGet("/v1.0/me", () => "api");
        WebUi.MapFallback(app);
        return app;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/w/0198f1c2-0000-7000-8000-000000000001/l/0198f1c2-0000-7000-8000-000000000002")]
    public async Task Client_routes_get_the_app_with_security_headers(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=root", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hashed_assets_are_cached_and_other_files_revalidated()
    {
        var asset = await _client.GetAsync("/assets/app-3f2a.js", TestContext.Current.CancellationToken);
        var icon = await _client.GetAsync("/favicon.svg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.True(asset.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromDays(365), asset.Headers.CacheControl?.MaxAge);
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.True(icon.Headers.CacheControl?.NoCache);
    }

    [Theory]
    [InlineData("GET", "/v1.0/unknown")]
    [InlineData("GET", "/connect/unknown")]
    [InlineData("GET", "/.well-known/unknown")]
    [InlineData("POST", "/somewhere")]
    public async Task Server_paths_and_other_methods_are_not_the_app(string method, string path)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("api", await _client.GetStringAsync("/v1.0/me", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Sign_in_goes_to_the_app_login_page_unless_configured()
    {
        Assert.Equal("/login", _app!.Services.GetRequiredService<IOptions<AuthOptions>>().Value.LoginUrl);

        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Web:RootPath"] = _root;
        builder.Configuration["Auth:LoginUrl"] = "https://sso.example.com/login";
        builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section);
        WebUi.AddServices(builder.Services, builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal("https://sso.example.com/login", provider.GetRequiredService<IOptions<AuthOptions>>().Value.LoginUrl);
    }

    [Fact]
    public void Without_a_built_app_nothing_is_served()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Web:RootPath"] = Path.Combine(_root, "missing");

        Assert.Null(WebUi.RootPath(builder.Configuration, builder.Environment));
    }
}
