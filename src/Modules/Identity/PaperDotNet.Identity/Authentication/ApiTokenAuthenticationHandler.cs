using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Authentication;

/// <summary>Authenticates <c>Authorization: Bearer pdn_…</c> personal access tokens.</summary>
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
        var token = GetBearerToken(Request.Headers[HeaderNames.Authorization].ToString());
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

    internal static string? GetBearerToken(string header) =>
        header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
}
