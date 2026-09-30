using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;
using PaperDotNet.Identity.Features;

namespace PaperDotNet.Identity.Authentication;

/// <summary>
/// The server's authentication scheme: <c>Authorization: Bearer pdn_…</c> is a personal access token (looked up by
/// the hash of its secret); any other bearer token is an access token from <c>/connect/token</c>, valid while its
/// security stamp is current (a password change, reset, disabling or deletion ends it). Challenges and forbids are
/// the bearer token handler's.
/// </summary>
internal sealed class PaperDotNetAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IdentityDbContext db,
    IMemoryCache cache,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PaperDotNet";

    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StampLifetime = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers[HeaderNames.Authorization].ToString();
        var bearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        if (ApiTokenSecret.LooksLikeToken(bearer))
        {
            return await AuthenticateApiTokenAsync(bearer!);
        }

        var result = await Context.AuthenticateAsync(BearerTokenDefaults.AuthenticationScheme);
        if (!result.Succeeded)
        {
            return result;
        }

        var principal = result.Principal;
        if (principal.FindFirst(PaperDotNetClaims.SecurityStamp)?.Value is not { } stamp
            || principal.FindGuid(PaperDotNetClaims.TenantId) is not { } tenantId
            || principal.FindGuid(PaperDotNetClaims.UserId) is not { } userId
            || await CurrentStampAsync(tenantId, userId) != stamp)
        {
            return AuthenticateResult.Fail("The session has ended.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, result.Properties, Scheme.Name));
    }

    private async Task<string> CurrentStampAsync(Guid tenantId, Guid userId)
    {
        var key = (Kind: "stamp", tenantId, userId, AccessGeneration.Current(tenantId));
        if (!cache.TryGetValue(key, out string? stamp) || stamp is null)
        {
            stamp = (await Users.FindAsync(db, tenantId, userId, Context.RequestAborted))?.SecurityStamp ?? "";
            cache.Set(key, stamp, StampLifetime);
        }

        return stamp;
    }

    private async Task<AuthenticateResult> AuthenticateApiTokenAsync(string secret)
    {
        var context = db;
        var hash = ApiTokenSecret.Hash(secret);
        var ct = Context.RequestAborted;
        var token = await context.ApiTokens.Where(t => t.Hash == hash).FirstOrDefaultAsync(ct);
        var now = time.GetUtcNow();
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now)
        {
            return AuthenticateResult.Fail("Invalid or expired API token.");
        }

        if (token.LastUsedAt is null || now - token.LastUsedAt > LastUsedResolution)
        {
            token.LastUsedAt = now;
            await context.SaveChangesAsync(ct);
        }

        var identity = new ClaimsIdentity(Scheme.Name, PaperDotNetClaims.UserName, null);
        identity.AddClaim(new Claim(PaperDotNetClaims.UserId, token.UserId.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.TenantId, token.TenantId.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.TokenId, token.Id.ToString()));
        identity.AddClaim(new Claim(PaperDotNetClaims.Scope, token.Scopes));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
