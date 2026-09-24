using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Identity.Authentication;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues short-lived JWT access tokens for local accounts. Stage 1 of
/// authentication (ADR-0002); replaced by OpenIddict flows in P1.
/// </summary>
internal sealed class AccessTokenIssuer(IOptions<AuthOptions> options, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    public static SymmetricSecurityKey CreateKey(string signingKey) => new(Encoding.UTF8.GetBytes(signingKey));

    public AccessToken Issue(Guid userId, string? name, Guid tenantId, string tenantIdentifier)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var expires = now.Add(settings.AccessTokenLifetime);
        var claims = new List<Claim>
        {
            new(PaperDotNetClaims.UserId, userId.ToString()),
            new(PaperDotNetClaims.TenantId, tenantId.ToString()),
            new(PaperDotNetClaims.TenantIdentifier, tenantIdentifier),
        };
        if (!string.IsNullOrEmpty(name))
        {
            claims.Add(new Claim(PaperDotNetClaims.Name, name));
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(CreateKey(settings.SigningKey), SecurityAlgorithms.HmacSha256),
        });
        return new AccessToken(token, expires);
    }
}
