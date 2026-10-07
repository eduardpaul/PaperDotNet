using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Authentication;

/// <summary>
/// Authenticates <c>Authorization: Bearer pdn_…</c> personal access tokens, and under <see cref="ApiRoutes.Dav"/> also
/// <c>Authorization: Basic</c> with the token as the password (WebDAV clients, ADR-0047; the user name is ignored).
/// </summary>
internal sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IdentityDbContext db,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers[HeaderNames.Authorization].ToString();
        var token = GetBearerToken(header) ?? (ApiRoutes.IsDav(Request) ? GetBasicPassword(header) : null);
        if (!ApiTokenSecret.LooksLikeToken(token))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiTokenSecret.Hash(token!);
        var entity = await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Hash == hash, Context.RequestAborted);
        var now = time.GetUtcNow();
        if (entity is null || entity.RevokedAt is not null || entity.ExpiresAt <= now)
        {
            return AuthenticateResult.Fail("Invalid or expired API token.");
        }

        if (entity.LastUsedAt is null || now - entity.LastUsedAt > LastUsedResolution)
        {
            await db.ApiTokens.Where(t => t.Id == entity.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new(PaperDotNetClaims.UserId, entity.UserId.ToString()),
            new(PaperDotNetClaims.TenantId, entity.TenantId.ToString()),
            new(PaperDotNetClaims.TenantIdentifier, entity.TenantIdentifier),
            new(PaperDotNetClaims.TokenId, entity.Id.ToString()),
        };
        claims.AddRange(entity.Scopes.Select(s => new Claim(PaperDotNetClaims.TokenScope, s)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, PaperDotNetClaims.Name, null));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>WebDAV clients need a Basic challenge to ask for credentials (Windows: "Map network drive").</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (ApiRoutes.IsDav(Request))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Basic realm=\"PaperDotNet\", charset=\"UTF-8\"";
            return Task.CompletedTask;
        }

        return base.HandleChallengeAsync(properties);
    }

    internal static string? GetBearerToken(string header) =>
        header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;

    /// <summary>The password of <c>Authorization: Basic base64(user:password)</c>, or null.</summary>
    internal static string? GetBasicPassword(string header)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[512];
        if (!Convert.TryFromBase64String(header["Basic ".Length..].Trim(), buffer, out var length))
        {
            return null;
        }

        var credentials = Encoding.UTF8.GetString(buffer[..length]);
        var separator = credentials.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? null : credentials[(separator + 1)..];
    }
}
