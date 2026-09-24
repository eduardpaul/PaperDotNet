using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Identity.Authentication;

internal static class AuthenticationSetup
{
    public static void AddPaperDotNetAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(AuthSchemes.Default)
            .AddPolicyScheme(AuthSchemes.Default, "JWT or API token", o =>
                o.ForwardDefaultSelector = ctx =>
                    ApiTokenSecret.LooksLikeToken(ApiTokenAuthenticationHandler.GetBearerToken(ctx.Request.Headers[HeaderNames.Authorization].ToString()))
                        ? AuthSchemes.ApiToken
                        : JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(AuthSchemes.ApiToken, null)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = auth.Value.Issuer,
                    ValidAudience = auth.Value.Audience,
                    IssuerSigningKey = AccessTokenIssuer.CreateKey(auth.Value.SigningKey),
                    NameClaimType = PaperDotNetClaims.Name,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });
    }
}
