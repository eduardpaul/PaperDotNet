using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record MeResponse(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    Guid TenantId,
    string TenantIdentifier,
    IReadOnlyList<string> Scopes);

/// <summary>What users change on their own profile; the e-mail and user name are managed by administrators.</summary>
public sealed record UpdateMeRequest(string? DisplayName);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record ApiTokenResponse(
    Guid Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt);

public sealed record CreateApiTokenRequest(string? Name, IReadOnlyList<string>? Scopes, int? ExpiresInDays);

/// <summary>Returned once, at creation: the only time the secret is visible.</summary>
public sealed record CreatedApiTokenResponse(ApiTokenResponse Token, string Secret);

/// <summary>The signed-in user: profile, effective scopes, password and personal access tokens (API-05).</summary>
internal static class Me
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/v1.0/me").WithTags("Me").RequireAuthorization();
        me.MapGet("", GetAsync).WithName("GetMe");
        me.MapPatch("", UpdateAsync).WithName("UpdateMe");
        me.MapPost("/password", ChangePasswordAsync).WithName("ChangeMyPassword");
        me.MapGet("/apiTokens", ListTokensAsync).WithName("ListMyApiTokens");
        me.MapPost("/apiTokens", CreateTokenAsync).WithName("CreateMyApiToken");
        me.MapDelete("/apiTokens/{id:guid}", RevokeTokenAsync).WithName("RevokeMyApiToken");
    }

    private static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> GetAsync(Caller caller, IdentityDbContext db, IEffectiveScopeProvider scopes, CancellationToken cancellationToken) =>
        await Users.FindAsync(db, caller.TenantId, caller.UserId, cancellationToken) is { } user
            ? TypedResults.Ok(await ResponseAsync(db, scopes, user, cancellationToken))
            : ApiErrors.NotFound();

    /// <summary>Changes the caller's display name; an empty name falls back to the user name.</summary>
    private static async Task<Results<Ok<MeResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        UpdateMeRequest body, Caller caller, IdentityDbContext db, IEffectiveScopeProvider scopes, CancellationToken cancellationToken)
    {
        if (body.DisplayName is { Length: > 200 })
        {
            return ApiErrors.Validation("displayName", "The display name can have at most 200 characters.");
        }

        if (await Users.FindAsync(db, caller.TenantId, caller.UserId, cancellationToken) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        if (body.DisplayName is { } name)
        {
            user.DisplayName = name.Trim().Length == 0 ? user.UserName : name.Trim();
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await ResponseAsync(db, scopes, user, cancellationToken));
    }

    /// <summary>Changes the caller's password; other sessions end (API tokens stay).</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest body, Caller caller, IdentityDbContext db, IPasswordHasher<User> hasher, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await Users.FindAsync(db, caller.TenantId, caller.UserId, cancellationToken) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        if (user.PasswordHash.Length == 0 || body.CurrentPassword is null
            || hasher.VerifyHashedPassword(user, user.PasswordHash, body.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            return ApiErrors.Validation("currentPassword", "The current password is not correct.");
        }

        if (Users.CheckPassword(body.NewPassword) is { } error)
        {
            return ApiErrors.Validation("newPassword", error);
        }

        user.PasswordHash = hasher.HashPassword(user, body.NewPassword!);
        await Users.EndSessionsAsync(db, user, revokeApiTokens: false, time.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static ApiTokenResponse ToResponse(ApiToken t) =>
        new(t.Id, t.Name, t.Prefix, ScopeList.Parse(t.Scopes), t.CreatedAt, t.ExpiresAt, t.LastUsedAt);

    private static async Task<Ok<List<ApiTokenResponse>>> ListTokensAsync(Caller caller, IdentityDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var ct = cancellationToken;
        var tokens = await db.ApiTokens.Where(t => t.TenantId == tenant && t.UserId == user && t.RevokedAt == null).OrderBy(t => t.Id).ToListAsync(ct);
        return TypedResults.Ok(tokens.Select(ToResponse).ToList());
    }

    /// <summary>Creates a token limited to scopes the caller holds (an API token cannot create more tokens).</summary>
    private static async Task<Results<Created<CreatedApiTokenResponse>, ValidationProblem, ProblemHttpResult>> CreateTokenAsync(
        CreateApiTokenRequest body, Caller caller, HttpContext http, IdentityDbContext db, IEffectiveScopeProvider scopes, TimeProvider time, CancellationToken cancellationToken)
    {
        if (http.User.HasClaim(c => c.Type == PaperDotNetClaims.TokenId))
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "signInRequired", "API tokens are created by a signed-in user, not with another API token.");
        }

        if (body.Name?.Trim() is not { Length: > 0 and <= 200 } name)
        {
            return ApiErrors.Validation("name", "A name of up to 200 characters is required.");
        }

        if (body.Scopes is not { Count: > 0 } requested)
        {
            return ApiErrors.Validation("scopes", "A token needs at least one scope.");
        }

        if (body.ExpiresInDays is < 1 or > 3650)
        {
            return ApiErrors.Validation("expiresInDays", "A token expires after 1 to 3650 days.");
        }

        var granted = await scopes.GetScopesAsync(caller.TenantId, caller.UserId, cancellationToken) ?? new HashSet<string>();
        if (requested.Where(s => !granted.Contains(s)).ToList() is { Count: > 0 } notGranted)
        {
            return ApiErrors.Validation("scopes", $"You can only grant scopes you hold. Not held: {string.Join(", ", notGranted)}.");
        }

        var (secret, prefix, hash) = ApiTokenSecret.Create();
        var now = time.GetUtcNow();
        var token = new ApiToken
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            Name = name,
            Prefix = prefix,
            Hash = hash,
            Scopes = ScopeList.Format(requested),
            CreatedAt = now,
            ExpiresAt = body.ExpiresInDays is { } days ? now.AddDays(days) : null,
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v1.0/me/apiTokens/{token.Id}", new CreatedApiTokenResponse(ToResponse(token), secret));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeTokenAsync(Guid id, Caller caller, IdentityDbContext database, TimeProvider time, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var tokenId = id;
        var ct = cancellationToken;
        var token = await db.ApiTokens.Where(t => t.TenantId == tenant && t.UserId == user && t.Id == tokenId && t.RevokedAt == null).FirstOrDefaultAsync(ct);
        if (token is null)
        {
            return ApiErrors.NotFound();
        }

        token.RevokedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<MeResponse> ResponseAsync(IdentityDbContext database, IEffectiveScopeProvider scopes, User user, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = user.TenantId;
        var ct = cancellationToken;
        var identifier = await db.Tenants.Where(t => t.Id == tenant).Select(t => t.Identifier).FirstOrDefaultAsync(ct) ?? "";
        var granted = await scopes.GetScopesAsync(user.TenantId, user.Id, ct) ?? new HashSet<string>();
        return new MeResponse(user.Id, user.UserName, user.DisplayName, user.Email, user.TenantId, identifier, [.. granted.Order(StringComparer.Ordinal)]);
    }
}
