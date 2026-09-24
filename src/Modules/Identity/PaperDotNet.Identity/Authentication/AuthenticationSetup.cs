using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Authentication;

internal static class AuthenticationSetup
{
    public static void AddPaperDotNetAuthentication(this IServiceCollection services)
    {
        // Key ring in the database: every node can read cookies, tokens and protected secrets.
        services.AddDataProtection()
            .SetApplicationName("PaperDotNet")
            .PersistKeysToDbContext<IdentityDbContext>();

        services.AddAuthentication(AuthSchemes.Default)
            .AddPolicyScheme(AuthSchemes.Default, "OAuth access token or API token", o =>
                o.ForwardDefaultSelector = ctx =>
                    ApiTokenSecret.LooksLikeToken(ApiTokenAuthenticationHandler.GetBearerToken(ctx.Request.Headers[HeaderNames.Authorization].ToString()))
                        ? AuthSchemes.ApiToken
                        : OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(AuthSchemes.ApiToken, null)
            .AddCookie(AuthSchemes.Session, o =>
            {
                o.Cookie.Name = "pdn.session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;

                // An API has no login page: answer 401/403 instead of redirecting.
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore()
                .UseDbContext<IdentityDbContext>()
                .ReplaceDefaultEntities<OAuthApplication, OAuthAuthorization, OAuthScope, OAuthToken, Guid>())
            .AddServer(o =>
            {
                o.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetUserInfoEndpointUris("connect/userinfo")
                    .SetEndSessionEndpointUris("connect/logout")
                    .SetRevocationEndpointUris("connect/revoke");
                o.AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange()
                    .AllowRefreshTokenFlow()
                    .AllowClientCredentialsFlow()
                    .AllowPasswordFlow();

                // Access tokens, codes and refresh tokens use Data Protection (key ring in the DB);
                // identity tokens are JWTs signed with the persisted key (ServerKeyStore).
                o.UseDataProtection();
                o.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseDataProtection();
                o.UseAspNetCore();
            });

        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, ServerKeyStore>();
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, ServerOptionsSetup>();
        services.AddSingleton<IConfigureOptions<OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreOptions>, ServerOptionsSetup>();
    }
}

/// <summary>Server settings that depend on configuration and on the scope catalog (including extension scopes).</summary>
internal sealed class ServerOptionsSetup(IOptions<AuthOptions> auth, IScopeCatalog catalog)
    : IConfigureOptions<OpenIddictServerOptions>, IConfigureOptions<OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreOptions>
{
    public void Configure(OpenIddictServerOptions options)
    {
        options.AccessTokenLifetime = auth.Value.AccessTokenLifetime;
        options.RefreshTokenLifetime = auth.Value.RefreshTokenLifetime;
        options.Scopes.UnionWith([
            OpenIddictConstants.Scopes.OpenId, OpenIddictConstants.Scopes.Profile, OpenIddictConstants.Scopes.OfflineAccess,
            OAuthScopes.Api, .. catalog.All.Select(s => s.Name)]);
    }

    public void Configure(OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreOptions options) =>
        options.DisableTransportSecurityRequirement = !auth.Value.RequireHttps;
}
